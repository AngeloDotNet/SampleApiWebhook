using System.Security.Cryptography;
using System.Text;

namespace Webhook.Api.Helpers;

public static class CryptoHelpers
{
    public static byte[] EncryptForStorage(byte[] plaintext, byte[] keyBytes)
    {
        if (keyBytes.Length != 32)
        {
            throw new ArgumentException("Data key must be 32 bytes (base64-encoded).");
        }

        var nonce = RandomNumberGenerator.GetBytes(12);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];

        using var aes = new AesGcm(keyBytes, AesGcm.TagByteSizes.MaxSize);
        aes.Encrypt(nonce, plaintext, ciphertext, tag, null);

        var outb = new byte[nonce.Length + tag.Length + ciphertext.Length];

        Buffer.BlockCopy(nonce, 0, outb, 0, nonce.Length);
        Buffer.BlockCopy(tag, 0, outb, nonce.Length, tag.Length);
        Buffer.BlockCopy(ciphertext, 0, outb, nonce.Length + tag.Length, ciphertext.Length);

        return outb;
    }

    public static byte[] DecryptFromStorage(byte[] stored, byte[] keyBytes)
    {
        if (keyBytes.Length != 32)
        {
            throw new ArgumentException("Data key must be 32 bytes (base64-encoded).");
        }

        if (stored.Length < 12 + 16)
        {
            throw new ArgumentException("Invalid stored payload");
        }

        var nonce = stored.AsSpan(0, 12).ToArray();
        var tag = stored.AsSpan(12, 16).ToArray();
        var ciphertext = stored.AsSpan(28).ToArray();
        var plaintext = new byte[ciphertext.Length];

        using var aes = new AesGcm(keyBytes, AesGcm.TagByteSizes.MaxSize);
        aes.Decrypt(nonce, ciphertext, tag, plaintext, null);

        return plaintext;
    }

    public static bool VerifyHmacSha256WithTimestamp(byte[] bodyBytes, long timestampSeconds, string secret, string signatureHeader)
    {
        const string prefix = "sha256=";

        if (string.IsNullOrEmpty(signatureHeader) || !signatureHeader.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var signatureHex = signatureHeader[prefix.Length..];
        byte[] expected;

        try
        {
            expected = ConvertHexStringToBytes(signatureHex);
        }
        catch
        {
            return false;
        }

        var timestampBytes = Encoding.UTF8.GetBytes(timestampSeconds.ToString());
        var dot = "."u8.ToArray();
        var msg = new byte[timestampBytes.Length + dot.Length + bodyBytes.Length];

        Buffer.BlockCopy(timestampBytes, 0, msg, 0, timestampBytes.Length);
        Buffer.BlockCopy(dot, 0, msg, timestampBytes.Length, dot.Length);
        Buffer.BlockCopy(bodyBytes, 0, msg, timestampBytes.Length + dot.Length, bodyBytes.Length);

        var keyBytes = Encoding.UTF8.GetBytes(secret);
        using var hmac = new HMACSHA256(keyBytes);
        var actual = hmac.ComputeHash(msg);

        if (actual.Length != expected.Length)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    public static byte[] ConvertHexStringToBytes(string hex)
    {
        if (hex.Length % 2 != 0)
        {
            throw new ArgumentException("Invalid hex length");
        }

        var bytes = new byte[hex.Length / 2];

        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        }

        return bytes;
    }

    public static TimeSpan ComputeBackoffSeconds(int attempts)
    {
        var baseSeconds = 2.0;
        var backoff = Math.Min(3600, baseSeconds * Math.Pow(2, attempts - 1));

        var rnd = RandomNumberGenerator.GetInt32(80) - 40; // -40..39
        var jitterFactor = 1.0 + rnd / 200.0;

        var seconds = Math.Max(1.0, backoff * jitterFactor);

        return TimeSpan.FromSeconds(seconds);
    }
}