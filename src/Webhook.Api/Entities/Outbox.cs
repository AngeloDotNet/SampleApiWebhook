namespace Webhook.Api.Entities;

public class Outbox
{
    public long Id { get; set; }
    public byte[] Payload { get; set; } = []; // nonce|tag|ciphertext
    public string? ContentType { get; set; }
    public string? HeadersJson { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset NextAttemptUtc { get; set; }
    public DateTimeOffset? LockedUntil { get; set; }
    public string? LockOwner { get; set; }
}