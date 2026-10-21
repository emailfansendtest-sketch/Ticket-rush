using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace TicketRush.BuildingBlocks.Contracts.Security;

/// <summary>
/// Signed bearer capability for checkout admission. This proves possession of a
/// Booking-issued hold receipt; it is NOT a substitute for authenticating a buyer.
/// Do not place proofs in URLs, logs, or publicly accessible GET hold responses.
/// </summary>
public sealed class HoldCheckoutProof
{
    private readonly byte[] key;

    public HoldCheckoutProof(string? keyBase64)
    {
        if (string.IsNullOrWhiteSpace(keyBase64))
            throw new InvalidOperationException("HoldProof:KeyBase64 must be configured on both Booking and Ordering APIs.");
        try { key = Convert.FromBase64String(keyBase64); }
        catch (FormatException e) { throw new InvalidOperationException("HoldProof:KeyBase64 must contain Base64 key material.", e); }
        if (key.Length < 32)
            throw new InvalidOperationException("HoldProof:KeyBase64 must decode to at least 32 bytes.");
    }

    public string Issue(Guid holdId, Guid buyerId, DateTimeOffset expiresAtUtc)
    {
        if (holdId == Guid.Empty || buyerId == Guid.Empty)
            throw new ArgumentException("Hold and buyer IDs must be nonempty.");
        // Expiration is signed using the exact .NET UTC tick count already persisted
        // by Booking, so services do not disagree about rounding to seconds.
        var content = $"v1|{holdId:D}|{buyerId:D}|{expiresAtUtc.UtcTicks.ToString(CultureInfo.InvariantCulture)}";
        var data = Encoding.UTF8.GetBytes(content);
        var mac = HMACSHA256.HashData(key, data);
        return $"{Base64Url(data)}.{Base64Url(mac)}";
    }

    public bool Verify(string? proof, Guid holdId, Guid buyerId, DateTimeOffset nowUtc)
    {
        if (holdId == Guid.Empty || buyerId == Guid.Empty ||
            string.IsNullOrEmpty(proof) || proof.Length > 512)
            return false;
        var parts = proof.Split('.');
        if (parts.Length != 2) return false;
        byte[] data, mac;
        try
        {
            data = Decode(parts[0]);
            mac = Decode(parts[1]);
        }
        catch (FormatException) { return false; }
        if (mac.Length != 32 || data.Length > 256) return false;

        // Perform signature verification before trusting any embedded claims.
        var expected = HMACSHA256.HashData(key, data);
        if (!CryptographicOperations.FixedTimeEquals(expected, mac)) return false;
        var claims = Encoding.UTF8.GetString(data).Split('|');
        if (claims.Length != 4 || claims[0] != "v1" ||
            !Guid.TryParseExact(claims[1], "D", out var signedHold) ||
            !Guid.TryParseExact(claims[2], "D", out var signedBuyer) ||
            !long.TryParse(claims[3], NumberStyles.None, CultureInfo.InvariantCulture, out var expiryTicks) ||
            expiryTicks <= 0 || expiryTicks > DateTime.MaxValue.Ticks)
            return false;
        return signedHold == holdId && signedBuyer == buyerId && nowUtc.UtcTicks < expiryTicks;
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Decode(string value)
    {
        // Disallow non-canonical spellings and overly large user-controlled input.
        if (value.Length > 350 || value.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_'))
            throw new FormatException("Invalid base64url character.");
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - (padded.Length % 4)) % 4);
        var decoded = Convert.FromBase64String(padded);
        if (Base64Url(decoded) != value) throw new FormatException("Noncanonical base64url encoding.");
        return decoded;
    }
}
