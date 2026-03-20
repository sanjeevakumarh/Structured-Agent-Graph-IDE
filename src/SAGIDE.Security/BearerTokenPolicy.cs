using System.Security.Cryptography;
using System.Text;
using SAGIDE.Core.Interfaces;

namespace SAGIDE.Security;

/// <summary>
/// Validates requests using a static shared secret bearer token.
///
/// Uses HMAC-then-compare to prevent timing side-channels:
/// <c>CryptographicOperations.FixedTimeEquals</c> alone short-circuits on length
/// mismatch, leaking the token length. HMAC normalises both values to a fixed-length
/// MAC before the constant-time comparison.
///
/// When <paramref name="token"/> is null or empty the policy is disabled and every
/// request is considered authorised — suitable for local development.
/// </summary>
public sealed class BearerTokenPolicy : ISecurityPolicy
{
    private readonly byte[]? _hmacKey;
    private readonly byte[]? _expectedMac;
    private readonly bool _enabled;

    public int UnauthorisedStatusCode      => 401;
    public string WwwAuthenticateChallenge => "Bearer realm=\"SAGIDE\"";

    public BearerTokenPolicy(string? token)
    {
        if (string.IsNullOrEmpty(token))
        {
            _enabled = false;
            return;
        }

        _enabled    = true;
        _hmacKey    = RandomNumberGenerator.GetBytes(32);
        var expected = $"Bearer {token}";
        _expectedMac = HMACSHA256.HashData(_hmacKey, Encoding.UTF8.GetBytes(expected));
    }

    public bool IsAuthorised(string? authorizationHeader)
    {
        if (!_enabled) return true;

        var header     = authorizationHeader ?? string.Empty;
        var headerMac  = HMACSHA256.HashData(_hmacKey!, Encoding.UTF8.GetBytes(header));
        return CryptographicOperations.FixedTimeEquals(headerMac, _expectedMac);
    }
}
