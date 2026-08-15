namespace Webhook.Api.Models;

public record class PublisherConfig
{
    public string RabbitHost { get; init; } = "";
    public string RabbitUser { get; init; } = "";
    public string RabbitPass { get; init; } = "";
    public string RabbitQueue { get; init; } = "";
    public int BatchSize { get; init; } = 20;
    public int MaxAttempts { get; init; } = 10;
    public TimeSpan ConfirmTimeout { get; init; } = TimeSpan.FromSeconds(10);
}