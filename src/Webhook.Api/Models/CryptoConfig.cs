namespace Webhook.Api.Models;

public record class CryptoConfig
{
    public byte[] DataKey { get; init; } = [];
}