using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Prometheus;
using RabbitMQ.Client;
using Webhook.Api.Data;
using Webhook.Api.Entities;
using Webhook.Api.Helpers;
using Webhook.Api.Models;
using Webhook.Api.Services;
using Webhook.Api.Settings;

namespace Webhook.Api;

public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.Services.AddOpenApi();

        // Load configuration
        var configuration = builder.Configuration;
        var appSettings = configuration.Get<AppSettings>() ?? throw new InvalidOperationException("Missing configuration (appsettings)");

        if (string.IsNullOrWhiteSpace(appSettings.Webhook?.Secret))
        {
            throw new InvalidOperationException("Webhook:Secret is required in configuration");
        }

        if (string.IsNullOrWhiteSpace(appSettings.Outbox?.DataKeyB64))
        {
            throw new InvalidOperationException("Outbox:DataKeyB64 is required in configuration");
        }

        if (string.IsNullOrWhiteSpace(appSettings.ConnectionStrings?.DefaultConnection))
        {
            throw new InvalidOperationException("ConnectionStrings:DefaultConnection is required in configuration");
        }

        if (string.IsNullOrWhiteSpace(appSettings.RabbitMq?.Host))
        {
            throw new InvalidOperationException("RabbitMq:Host is required in configuration");
        }

        // decode data key
        var dataKey = Convert.FromBase64String(appSettings.Outbox!.DataKeyB64);

        if (dataKey.Length != 32)
        {
            throw new InvalidOperationException("Outbox:DataKeyB64 must be base64 of 32 bytes (AES-256 key)");
        }

        // EF Core DbContext (Npgsql)
        builder.Services.AddDbContextFactory<OutboxDbContext>(options =>
        {
            options.UseNpgsql(appSettings.ConnectionStrings!.DefaultConnection, npg =>
            {
                npg.EnableRetryOnFailure();
            });
        });

        // register configs and background services
        builder.Services.AddSingleton(new PublisherConfig
        {
            RabbitHost = appSettings.RabbitMq!.Host!,
            RabbitUser = appSettings.RabbitMq.Username!,
            RabbitPass = appSettings.RabbitMq.Password!,
            RabbitQueue = appSettings.RabbitMq.Queue!,
            BatchSize = appSettings.Outbox!.BatchSize,
            MaxAttempts = appSettings.Outbox.MaxAttempts,
            ConfirmTimeout = TimeSpan.FromSeconds(appSettings.RabbitMq.ConfirmTimeoutSeconds)
        });

        builder.Services.AddSingleton(new Models.CryptoConfig { DataKey = dataKey });
        builder.Services.AddHostedService<WebhookPublisherService>();

        // Register Outbox metrics service
        builder.Services.AddSingleton(OutboxMetrics.Gauge); // register gauge instance if needed
        builder.Services.AddHostedService<OutboxMetricsService>();

        var app = builder.Build();
        app.UseHttpsRedirection();

        if (app.Environment.IsDevelopment())
        {
            app.MapOpenApi();
            app.UseSwaggerUI();
        }

        // Use Prometheus middleware: collects HTTP metrics and exposes /metrics
        app.UseHttpMetrics();
        app.MapMetrics(); // exposes /metrics

        // Ensure DB exists (demo). In prod use migrations.
        using (var scope = app.Services.CreateScope())
        {
            var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<OutboxDbContext>>();
            using var db = dbFactory.CreateDbContext();

            //db.Database.EnsureCreated();
            db.Database.Migrate();
        }

        // Health endpoint
        app.MapGet("/health", async (IServiceProvider services, CancellationToken ct) =>
        {
            var dbFactory = services.GetRequiredService<IDbContextFactory<OutboxDbContext>>();
            var cfg = services.GetRequiredService<PublisherConfig>();

            var result = new Dictionary<string, object>();
            var isHealthy = true;

            // DB check
            try
            {
                using var db = dbFactory.CreateDbContext();
                await db.Database.ExecuteSqlRawAsync("SELECT 1", ct);

                result["database"] = "ok";
            }
            catch (Exception ex)
            {
                isHealthy = false;
                result["database"] = $"error: {ex.Message}";
            }

            // RabbitMQ check (lightweight connect + close)
            try
            {
                var factory = new ConnectionFactory
                {
                    HostName = cfg.RabbitHost,
                    UserName = cfg.RabbitUser,
                    Password = cfg.RabbitPass,
                    RequestedConnectionTimeout = TimeSpan.FromMilliseconds(2000) // 2s
                };

                using var conn = factory.CreateConnection();
                using var ch = conn.CreateModel();

                result["rabbitmq"] = "ok";
            }
            catch (Exception ex)
            {
                isHealthy = false;
                result["rabbitmq"] = $"error: {ex.Message}";
            }

            // Outbox pending count
            try
            {
                using var db = dbFactory.CreateDbContext();
                var pending = await db.Outbox.CountAsync(o => o.NextAttemptUtc <= DateTimeOffset.UtcNow, ct);

                result["outbox_pending"] = pending;
            }
            catch (Exception ex)
            {
                result["outbox_pending"] = $"error: {ex.Message}";
            }

            result["timestamp"] = DateTimeOffset.UtcNow;

            if (isHealthy)
            {
                return Results.Ok(result);
            }

            return Results.Json(result, statusCode: StatusCodes.Status503ServiceUnavailable);
        });

        // Minimal webhook endpoint
        app.MapPost("/webhook", async (HttpRequest req) =>
        {
            // read body as bytes
            using var ms = new MemoryStream();
            await req.Body.CopyToAsync(ms);

            var bodyBytes = ms.ToArray();

            // timestamp header
            if (!req.Headers.TryGetValue("X-Webhook-Timestamp", out var tsVal))
            {
                return Results.Unauthorized();
            }

            if (!long.TryParse(tsVal.ToString(), out var tsSeconds))
            {
                return Results.BadRequest("Invalid timestamp");
            }

            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (Math.Abs(now - tsSeconds) > (appSettings.Webhook?.AllowSkewSeconds ?? 300))
            {
                return Results.Unauthorized();
            }

            // signature header
            if (!req.Headers.TryGetValue("X-Webhook-Signature", out var sigVal))
            {
                return Results.Unauthorized();
            }

            var signatureHeader = sigVal.ToString();

            if (!VerifyHmacSha256WithTimestamp(bodyBytes, tsSeconds, appSettings.Webhook!.Secret, signatureHeader))
            {
                return Results.Unauthorized();
            }

            // Persist to outbox (encrypt payload)
            var dbFactory = req.HttpContext.RequestServices.GetRequiredService<IDbContextFactory<OutboxDbContext>>();
            var headersJson = JsonSerializer.Serialize(req.Headers.ToDictionary(h
                => h.Key, h => (string)h.Value!));

            var storedBytes = CryptoHelpers.EncryptForStorage(bodyBytes, dataKey);
            var outbox = new Outbox
            {
                Payload = storedBytes,
                ContentType = req.ContentType ?? "application/octet-stream",
                HeadersJson = headersJson,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                Attempts = 0,
                NextAttemptUtc = DateTimeOffset.UtcNow
            };

            using (var db = dbFactory.CreateDbContext())
            {
                db.Outbox.Add(outbox);
                await db.SaveChangesAsync();
            }

            return Results.Accepted();
        });

        app.Run();
    }

    private static bool VerifyHmacSha256WithTimestamp(byte[] body, long timestamp, string secret, string signature)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));

        var timestampBytes = Encoding.UTF8.GetBytes(timestamp.ToString());
        var combined = new byte[timestampBytes.Length + body.Length];

        Buffer.BlockCopy(timestampBytes, 0, combined, 0, timestampBytes.Length);
        Buffer.BlockCopy(body, 0, combined, timestampBytes.Length, body.Length);

        var hash = hmac.ComputeHash(combined);
        var computed = Convert.ToHexString(hash).ToLowerInvariant();

        return signature.Equals(computed, StringComparison.OrdinalIgnoreCase);
    }
}