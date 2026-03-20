namespace SAGIDE.Core.Interfaces;

/// <summary>
/// Evaluates whether an inbound HTTP request is authorised to access the API.
///
/// The default implementation is <c>BearerTokenPolicy</c> in <c>SAGIDE.Security</c>
/// which performs an HMAC-normalised constant-time token comparison to prevent
/// timing side-channels. Future implementations could add RBAC, mTLS, or API keys.
/// </summary>
public interface ISecurityPolicy
{
    /// <summary>
    /// Returns true when the request is authorised to proceed.
    /// Called once per /api/* request before any handler runs.
    /// </summary>
    bool IsAuthorised(string? authorizationHeader);

    /// <summary>
    /// HTTP status code to return when <see cref="IsAuthorised"/> returns false.
    /// Typically 401 for missing/invalid credentials.
    /// </summary>
    int UnauthorisedStatusCode { get; }

    /// <summary>
    /// Value for the WWW-Authenticate response header when the request is rejected.
    /// </summary>
    string WwwAuthenticateChallenge { get; }
}
