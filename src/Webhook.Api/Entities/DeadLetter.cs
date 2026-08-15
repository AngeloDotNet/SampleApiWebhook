namespace Webhook.Api.Entities;

public class DeadLetter
{
    public long Id { get; set; }
    public long? OriginalId { get; set; }
    public byte[]? Payload { get; set; }
    public string? ContentType { get; set; }
    public string? HeadersJson { get; set; }
    public DateTimeOffset? CreatedAtUtc { get; set; }
    public DateTimeOffset FailedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public int Attempts { get; set; }
    public string? Error { get; set; }
}