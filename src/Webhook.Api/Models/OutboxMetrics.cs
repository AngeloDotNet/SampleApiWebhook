using Prometheus;

namespace Webhook.Api.Models;

public static class OutboxMetrics
{
    public static readonly Gauge Gauge = Metrics.CreateGauge("outbox_pending", "Number of pending messages in the outbox (NextAttemptUtc <= now)");
}