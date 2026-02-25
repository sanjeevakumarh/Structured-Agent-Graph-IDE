using Microsoft.Extensions.Logging.Abstractions;
using SAGIDE.Service.Providers;

namespace SAGIDE.Service.Tests;

/// <summary>
/// Unit tests for <see cref="OllamaHostHealthMonitor.TryGetBestHost"/> routing logic.
///
/// State is injected via the <c>internal SimulateHostState</c> test seam so that
/// no real HTTP calls are made and no background timer runs.
/// </summary>
public class OllamaHostHealthMonitorTests
{
    private const string Host1 = "http://host1:11434";
    private const string Host2 = "http://host2:11434";
    private const string Host3 = "http://host3:11434";

    private static OllamaHostHealthMonitor Make(params string[] knownUrls)
        => new(knownUrls, NullLogger<OllamaHostHealthMonitor>.Instance);

    // ── All hosts unknown ─────────────────────────────────────────────────────

    [Fact]
    public void AllHostsUnknown_ReturnsNull()
    {
        var mon = Make(Host1, Host2);
        // No SimulateHostState calls → default IsReachable=false

        var result = mon.TryGetBestHost("llama3:8b", Host1, [Host1, Host2]);

        Assert.Null(result);
    }

    // ── Rule 1: preferred host reachable ─────────────────────────────────────

    [Fact]
    public void PreferredReachable_ReturnsPreferred()
    {
        var mon = Make(Host1, Host2);
        mon.SimulateHostState(Host1, isReachable: true,  loadedModels: []);
        mon.SimulateHostState(Host2, isReachable: true,  loadedModels: ["llama3:8b"]);

        // Even though Host2 has the model warm, Host1 (preferred) is reachable → Rule 1 wins
        var result = mon.TryGetBestHost("llama3:8b", Host1, [Host1, Host2]);

        Assert.Equal(Host1.TrimEnd('/'), result);
    }

    // ── Rule 2: preferred unreachable, warm host exists ───────────────────────

    [Fact]
    public void PreferredUnreachable_WarmHostExists_ReturnsWarmHost()
    {
        var mon = Make(Host1, Host2);
        mon.SimulateHostState(Host1, isReachable: false, loadedModels: []);
        mon.SimulateHostState(Host2, isReachable: true,  loadedModels: ["deepseek-coder:6.7b"]);

        var result = mon.TryGetBestHost("deepseek-coder:6.7b", Host1, [Host1, Host2]);

        Assert.Equal(Host2.TrimEnd('/'), result);
    }

    [Fact]
    public void WarmHostSelection_CaseInsensitive()
    {
        var mon = Make(Host1, Host2);
        mon.SimulateHostState(Host1, isReachable: false, loadedModels: []);
        mon.SimulateHostState(Host2, isReachable: true,  loadedModels: ["Llama3:8B"]);

        // Query uses lowercase model name — matching must be case-insensitive
        var result = mon.TryGetBestHost("llama3:8b", Host1, [Host1, Host2]);

        Assert.Equal(Host2.TrimEnd('/'), result);
    }

    // ── Rule 3: preferred unreachable, no warm host, but any reachable ────────

    [Fact]
    public void NoWarmHost_AnyReachable_ReturnsThatHost()
    {
        var mon = Make(Host1, Host2, Host3);
        mon.SimulateHostState(Host1, isReachable: false, loadedModels: []);
        mon.SimulateHostState(Host2, isReachable: false, loadedModels: []);
        mon.SimulateHostState(Host3, isReachable: true,  loadedModels: []);  // reachable but cold

        var result = mon.TryGetBestHost("llama3:8b", Host1, [Host1, Host2, Host3]);

        Assert.Equal(Host3.TrimEnd('/'), result);
    }

    // ── Rule 4: all unreachable → null ────────────────────────────────────────

    [Fact]
    public void AllUnreachable_ReturnsNull()
    {
        var mon = Make(Host1, Host2);
        mon.SimulateHostState(Host1, isReachable: false, loadedModels: []);
        mon.SimulateHostState(Host2, isReachable: false, loadedModels: []);

        var result = mon.TryGetBestHost("llama3:8b", Host1, [Host1, Host2]);

        Assert.Null(result);
    }

    // ── Empty candidates ──────────────────────────────────────────────────────

    [Fact]
    public void EmptyCandidates_ReturnsNull()
    {
        var mon = Make(Host1);
        mon.SimulateHostState(Host1, isReachable: true, loadedModels: ["llama3:8b"]);

        // Preferred not in empty candidates list
        var result = mon.TryGetBestHost("llama3:8b", "http://other:11434", []);

        Assert.Null(result);
    }

    // ── IsReachable / GetLoadedModels ─────────────────────────────────────────

    [Fact]
    public void IsReachable_ReflectsSimulatedState()
    {
        var mon = Make(Host1, Host2);
        mon.SimulateHostState(Host1, isReachable: true,  loadedModels: []);
        mon.SimulateHostState(Host2, isReachable: false, loadedModels: []);

        Assert.True(mon.IsReachable(Host1));
        Assert.False(mon.IsReachable(Host2));
    }

    [Fact]
    public void GetLoadedModels_ReflectsSimulatedState()
    {
        var mon = Make(Host1);
        mon.SimulateHostState(Host1, isReachable: true,
            loadedModels: ["llama3:8b", "mistral:7b"]);

        var models = mon.GetLoadedModels(Host1);

        Assert.Equal(2, models.Count);
        Assert.Contains("llama3:8b", models);
        Assert.Contains("mistral:7b", models);
    }

    [Fact]
    public void GetLoadedModels_UnknownHost_ReturnsEmpty()
    {
        var mon = Make(Host1);

        var models = mon.GetLoadedModels("http://unknown:11434");

        Assert.Empty(models);
    }

    // ── Trailing slash normalisation ──────────────────────────────────────────

    [Fact]
    public void TrailingSlash_Normalized_BeforeComparison()
    {
        var mon = Make("http://host1:11434/");
        mon.SimulateHostState("http://host1:11434/", isReachable: true, loadedModels: []);

        // Both with and without trailing slash should be treated the same
        Assert.True(mon.IsReachable("http://host1:11434"));
        Assert.True(mon.IsReachable("http://host1:11434/"));
    }
}
