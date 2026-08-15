namespace Webhook.Api.Settings;

public class AppSettings
{
    public ConnectionStrings? ConnectionStrings { get; set; }
    public WebhookOptions? Webhook { get; set; }
    public OutboxOptions? Outbox { get; set; }
    public RabbitMqOptions? RabbitMq { get; set; }
}

public class ConnectionStrings
{
    public string? DefaultConnection { get; set; }
}

public class WebhookOptions
{
    public string? Secret { get; set; }
    public int AllowSkewSeconds { get; set; } = 300;
}

public class OutboxOptions
{
    public string? DataKeyB64 { get; set; }
    public int BatchSize { get; set; } = 20;
    public int MaxAttempts { get; set; } = 10;
    public int MetricsIntervalSeconds { get; set; } = 15;
}

public class RabbitMqOptions
{
    public string? Host { get; set; }
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string? Queue { get; set; }
    public int ConfirmTimeoutSeconds { get; set; } = 10;
}