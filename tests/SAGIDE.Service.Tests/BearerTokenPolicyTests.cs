using SAGIDE.Security;

namespace SAGIDE.Service.Tests;

/// <summary>
/// Tests for <see cref="BearerTokenPolicy"/> covering:
/// - Disabled (passthrough) when no token is configured
/// - Correct token accepted
/// - Wrong token rejected
/// - Missing header rejected
/// - Token prefix required ("Bearer " prefix)
/// - Null header handled safely
/// - Different instances produce independent keys (no shared state)
/// - Status code and challenge header values
/// </summary>
public class BearerTokenPolicyTests
{
    // ── Disabled mode (no token configured) ──────────────────────────────────

    [Fact]
    public void NullToken_IsDisabled_AllowsAnyHeader()
    {
        var policy = new BearerTokenPolicy(null);
        Assert.True(policy.IsAuthorised("Bearer anything"));
        Assert.True(policy.IsAuthorised(string.Empty));
        Assert.True(policy.IsAuthorised(null));
    }

    [Fact]
    public void EmptyToken_IsDisabled_AllowsAnyHeader()
    {
        var policy = new BearerTokenPolicy(string.Empty);
        Assert.True(policy.IsAuthorised("Bearer anything"));
        Assert.True(policy.IsAuthorised(null));
    }

    // ── Enabled mode ──────────────────────────────────────────────────────────

    [Fact]
    public void CorrectBearerToken_IsAuthorised()
    {
        var policy = new BearerTokenPolicy("secret-token-123");
        Assert.True(policy.IsAuthorised("Bearer secret-token-123"));
    }

    [Fact]
    public void WrongToken_IsRejected()
    {
        var policy = new BearerTokenPolicy("correct-token");
        Assert.False(policy.IsAuthorised("Bearer wrong-token"));
    }

    [Fact]
    public void MissingHeader_IsRejected()
    {
        var policy = new BearerTokenPolicy("secret");
        Assert.False(policy.IsAuthorised(string.Empty));
    }

    [Fact]
    public void NullHeader_IsRejected()
    {
        var policy = new BearerTokenPolicy("secret");
        Assert.False(policy.IsAuthorised(null));
    }

    [Fact]
    public void TokenWithoutBearerPrefix_IsRejected()
    {
        // Raw token without "Bearer " prefix must be rejected
        var policy = new BearerTokenPolicy("mytoken");
        Assert.False(policy.IsAuthorised("mytoken"));
    }

    [Fact]
    public void TokenWithWrongCase_IsRejected()
    {
        // Header matching is case-sensitive (HMAC of full string)
        var policy = new BearerTokenPolicy("MyToken");
        Assert.False(policy.IsAuthorised("Bearer mytoken"));
        Assert.False(policy.IsAuthorised("bearer MyToken"));
    }

    [Fact]
    public void ExtraWhitespace_IsRejected()
    {
        var policy = new BearerTokenPolicy("token");
        Assert.False(policy.IsAuthorised("Bearer token "));
        Assert.False(policy.IsAuthorised(" Bearer token"));
    }

    // ── Constant-time property ────────────────────────────────────────────────
    // We can't measure timing in a unit test, but we can verify that two different
    // wrong tokens both return false — ensuring the comparison doesn't short-circuit
    // on obvious mismatches before HMAC normalisation.

    [Fact]
    public void ShortWrongToken_IsRejectedLikeLongWrongToken()
    {
        var policy = new BearerTokenPolicy("correct-very-long-token-value");
        Assert.False(policy.IsAuthorised("Bearer x"));
        Assert.False(policy.IsAuthorised("Bearer correct-very-long-token-valu")); // 1 char short
    }

    // ── Instance isolation ────────────────────────────────────────────────────

    [Fact]
    public void TwoInstances_SameToken_IndependentKeys_BothAcceptCorrectHeader()
    {
        // Each instance generates its own HMAC key — they must both accept the same token
        var p1 = new BearerTokenPolicy("shared-token");
        var p2 = new BearerTokenPolicy("shared-token");

        Assert.True(p1.IsAuthorised("Bearer shared-token"));
        Assert.True(p2.IsAuthorised("Bearer shared-token"));
    }

    [Fact]
    public void TwoInstances_DifferentTokens_CrossRejection()
    {
        var p1 = new BearerTokenPolicy("token-for-p1");
        var p2 = new BearerTokenPolicy("token-for-p2");

        Assert.False(p1.IsAuthorised("Bearer token-for-p2"));
        Assert.False(p2.IsAuthorised("Bearer token-for-p1"));
    }

    // ── Contract values ───────────────────────────────────────────────────────

    [Fact]
    public void UnauthorisedStatusCode_Is401()
    {
        var policy = new BearerTokenPolicy("t");
        Assert.Equal(401, policy.UnauthorisedStatusCode);
    }

    [Fact]
    public void WwwAuthenticateChallenge_ContainsBearerRealm()
    {
        var policy = new BearerTokenPolicy("t");
        Assert.Contains("Bearer", policy.WwwAuthenticateChallenge);
        Assert.Contains("SAGIDE", policy.WwwAuthenticateChallenge);
    }
}
