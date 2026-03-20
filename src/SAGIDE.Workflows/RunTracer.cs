using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;

namespace SAGIDE.Workflows;

/// <summary>
/// Writes a structured per-run trace to disk for pipeline debugging.
/// Each LLM call, search query, and step result is saved as a numbered file,
/// allowing step-by-step inspection of the data pipeline.
///
/// File layout: <c>{Path}/{domain}-{name}-{datestamp}/</c>
///   01-run-start.json
///   02-{stepName}-planning-prompt.txt
///   03-{stepName}-queries.json
///   04-{stepName}-search-q01.json  (query + raw results)
///   ...
///   NN-{stepName}-output.txt       (final step output stored in vars)
///   NN-subtask-{name}-prompt.txt
///   NN-subtask-{name}-result.txt
///
/// Enabled via <c>SAGIDE:RunTracing:Enabled = true</c> in appsettings.
/// Output path: <c>SAGIDE:RunTracing:Path</c> (default <c>~/reports/traces</c>).
/// </summary>
public sealed class RunTracer
{
    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly char[] _invalidChars = Path.GetInvalidFileNameChars();

    private readonly string? _dir;  // null = disabled
    private int _seq;

    public bool IsEnabled => _dir is not null;

    private RunTracer(string? dir) => _dir = dir;

    /// <summary>
    /// Creates a tracer for this run. Returns a disabled no-op tracer when
    /// <c>SAGIDE:RunTracing:Enabled</c> is false or missing.
    /// </summary>
    /// <param name="config">App configuration.</param>
    /// <param name="explicitDir">
    /// When non-null, use this directory directly (co-located with the report output file).
    /// When null, falls back to <c>SAGIDE:RunTracing:Path/{domain}-{name}-{datestamp}</c>.
    /// </param>
    public static RunTracer Create(
        IConfiguration config,
        string? explicitDir,
        string domain,
        string name,
        string datestamp)
    {
        if (!config.GetValue("SAGIDE:RunTracing:Enabled", false))
            return new RunTracer(null);

        string dir;
        if (!string.IsNullOrEmpty(explicitDir))
        {
            dir = explicitDir;
        }
        else
        {
            var basePath = config["SAGIDE:RunTracing:Path"] ?? "~/reports/traces";
            basePath = basePath.Replace("~",
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                StringComparison.Ordinal);
            dir = Path.Combine(basePath, $"{domain}-{name}-{datestamp}");
        }

        Directory.CreateDirectory(dir);
        return new RunTracer(dir);
    }

    /// <summary>Returns the folder path where trace files are written, or null when disabled.</summary>
    public string? FolderPath => _dir;

    /// <summary>Serialises <paramref name="payload"/> as indented JSON to the next numbered file.</summary>
    public void Write(string label, object? payload)
    {
        if (_dir is null) return;
        File.WriteAllText(NextPath(label, "json"), JsonSerializer.Serialize(payload, _jsonOpts));
    }

    /// <summary>Writes a plain text file (for prompts, LLM outputs, search snippets).</summary>
    public void WriteText(string label, string text)
    {
        if (_dir is null) return;
        File.WriteAllText(NextPath(label, "txt"), text);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private string NextPath(string label, string ext)
    {
        var n    = Interlocked.Increment(ref _seq);
        var safe = new string(label.Select(c => _invalidChars.Contains(c) ? '_' : c).ToArray());
        return Path.Combine(_dir!, $"{n:D2}-{safe}.{ext}");
    }
}
