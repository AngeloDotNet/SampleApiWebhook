using Microsoft.EntityFrameworkCore;
using Webhook.Api.Data;
using Webhook.Api.Models;

namespace Webhook.Api.Services;

public class OutboxMetricsService(IDbContextFactory<OutboxDbContext> dbFactory, IConfiguration configuration) : BackgroundService
{
    readonly int intervalSeconds = configuration.GetValue<int?>("Outbox:MetricsIntervalSeconds") ?? 15;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Console.WriteLine($"OutboxMetricsService starting, interval {intervalSeconds}s");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var db = dbFactory.CreateDbContext();
                var count = await db.Outbox.CountAsync(o => o.NextAttemptUtc <= DateTimeOffset.UtcNow, stoppingToken);

                OutboxMetrics.Gauge.Set(count);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"OutboxMetricsService error: {ex.Message}");

                // on error set gauge to -1 to signal problem (optional)
                try
                {
                    OutboxMetrics.Gauge.Set(-1);
                }
                catch { }
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), stoppingToken);
            }
            catch (TaskCanceledException)
            {
                break;
            }
        }
    }
}