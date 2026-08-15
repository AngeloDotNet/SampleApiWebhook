using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;
using Webhook.Api.Data;
using Webhook.Api.Entities;
using Webhook.Api.Helpers;
using Webhook.Api.Models;

namespace Webhook.Api.Services;

public class WebhookPublisherService : BackgroundService
{
    public IConnection? connection;
    public RabbitMQ.Client.IModel? channel;

    private readonly IDbContextFactory<OutboxDbContext> dbFactory;
    private readonly PublisherConfig cfg;

    private readonly CryptoConfig crypto;
    private readonly ConnectionFactory? rabbitFactory;

    private readonly string instanceId = Guid.NewGuid().ToString();

    public WebhookPublisherService(IDbContextFactory<OutboxDbContext> dbFactory, PublisherConfig cfg, CryptoConfig crypto)
    {
        this.dbFactory = dbFactory;
        this.cfg = cfg;
        this.crypto = crypto;
        rabbitFactory = new ConnectionFactory
        {
            HostName = cfg.RabbitHost,
            UserName = cfg.RabbitUser,
            Password = cfg.RabbitPass,
            DispatchConsumersAsync = false,
            AutomaticRecoveryEnabled = false
        };
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Console.WriteLine($"WebhookPublisherService starting (instance {instanceId})...");
        var reconnectDelay = TimeSpan.FromSeconds(2);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                EnsureConnected();

                List<Outbox> batch;

                using (var db = dbFactory.CreateDbContext())
                {
                    using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, stoppingToken);

                    var sql = FormattableStringFactory.Create(@"
                        SELECT * FROM outbox
                        WHERE next_attempt_utc <= now()
                        ORDER BY created_at_utc
                        LIMIT {0}
                        FOR UPDATE SKIP LOCKED", cfg.BatchSize);

                    batch = await db.Outbox.FromSqlInterpolated(sql).ToListAsync(stoppingToken);

                    if (batch.Count == 0)
                    {
                        await tx.RollbackAsync(stoppingToken);
                        await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);

                        continue;
                    }

                    var lockUntil = DateTimeOffset.UtcNow.AddMinutes(1);

                    foreach (var row in batch)
                    {
                        row.LockOwner = instanceId;
                        row.LockedUntil = lockUntil;
                    }

                    await db.SaveChangesAsync(stoppingToken);
                    await tx.CommitAsync(stoppingToken);
                }

                var ids = batch.Select(x => x.Id).ToArray();

                try
                {
                    PublishBatch(batch);

                    using (var db = dbFactory.CreateDbContext())
                    {
                        var rowsToDelete = db.Outbox.Where(o => ids.Contains(o.Id));

                        db.Outbox.RemoveRange(rowsToDelete);
                        await db.SaveChangesAsync(stoppingToken);
                    }

                    Console.WriteLine($"Published and removed {ids.Length} messages from outbox.");
                    reconnectDelay = TimeSpan.FromSeconds(2);
                }
                catch (Exception pubEx)
                {
                    Console.Error.WriteLine($"Publish error: {pubEx.Message}");

                    using (var db = dbFactory.CreateDbContext())
                    {
                        foreach (var row in batch)
                        {
                            var r = await db.Outbox.FindAsync(new object[] { row.Id }, cancellationToken: stoppingToken);

                            if (r == null)
                            {
                                continue;
                            }

                            r.Attempts += 1;

                            if (r.Attempts >= cfg.MaxAttempts)
                            {
                                var dl = new DeadLetter
                                {
                                    OriginalId = r.Id,
                                    Payload = r.Payload,
                                    ContentType = r.ContentType,
                                    HeadersJson = r.HeadersJson,
                                    CreatedAtUtc = r.CreatedAtUtc,
                                    Attempts = r.Attempts,
                                    Error = pubEx.ToString(),
                                    FailedAtUtc = DateTimeOffset.UtcNow
                                };

                                db.Add(dl);
                                db.Remove(r);
                            }
                            else
                            {
                                var next = DateTimeOffset.UtcNow.Add(ComputeBackoffSeconds(r.Attempts));

                                r.NextAttemptUtc = next;
                                r.LockOwner = null;
                                r.LockedUntil = null;
                            }
                        }

                        await db.SaveChangesAsync(stoppingToken);
                    }

                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                }
            }
            catch (BrokerUnreachableException brex)
            {
                Console.Error.WriteLine($"Broker unreachable: {brex.Message}. Reconnect in {reconnectDelay.TotalSeconds}s");
                CloseConnection();

                await Task.Delay(reconnectDelay, stoppingToken);
                reconnectDelay = TimeSpan.FromSeconds(Math.Min(300, reconnectDelay.TotalSeconds * 2));
            }
            catch (AlreadyClosedException acex)
            {
                Console.Error.WriteLine($"AMQP connection closed: {acex.Message}. Reconnect in {reconnectDelay.TotalSeconds}s");
                CloseConnection();

                await Task.Delay(reconnectDelay, stoppingToken);
                reconnectDelay = TimeSpan.FromSeconds(Math.Min(300, reconnectDelay.TotalSeconds * 2));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Publisher loop unexpected error: {ex.Message}. Sleeping 2s.");
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
        }

        CloseConnection();
    }

    void EnsureConnected()
    {
        if (connection != null && connection.IsOpen && channel != null && channel.IsOpen)
        {
            return;
        }

        CloseConnection();

        Console.WriteLine($"Connecting to RabbitMQ {cfg.RabbitHost}...");
        connection = rabbitFactory!.CreateConnection();
        channel = connection.CreateModel();
        channel.QueueDeclare(queue: cfg.RabbitQueue, durable: true, exclusive: false, autoDelete: false, arguments: null);
        channel.ConfirmSelect();
        Console.WriteLine("RabbitMQ connected and confirm mode enabled.");
    }

    void PublishBatch(List<Outbox> batch)
    {
        if (channel == null)
        {
            throw new InvalidOperationException("AMQP channel not connected");
        }

        foreach (var row in batch)
        {
            var plaintext = CryptoHelpers.DecryptFromStorage(row.Payload, crypto.DataKey);
            var props = channel.CreateBasicProperties();

            props.ContentType = row.ContentType ?? "application/octet-stream";
            props.DeliveryMode = 2;
            props.Headers = new Dictionary<string, object>
            {
                { "stored-created-at", row.CreatedAtUtc.ToUnixTimeSeconds() },
                { "original-headers", row.HeadersJson ?? "" }
            };

            channel.BasicPublish(exchange: "", routingKey: cfg.RabbitQueue, basicProperties: props, body: plaintext);
        }

        var ok = channel.WaitForConfirms(cfg.ConfirmTimeout);

        if (!ok)
        {
            throw new Exception("Publisher confirms not received for batch");
        }
    }

    void CloseConnection()
    {
        try
        {
            channel?.Close();
        }
        catch { }

        try
        {
            connection?.Close();
        }
        catch { }

        try
        {
            channel?.Dispose();
        }
        catch { }

        try
        {
            connection?.Dispose();
        }
        catch { }

        channel = null;
        connection = null;
    }

    static TimeSpan ComputeBackoffSeconds(int attempts)
    {
        var seconds = Math.Pow(2, attempts);
        return TimeSpan.FromSeconds(Math.Min(seconds, 3600));
    }
}