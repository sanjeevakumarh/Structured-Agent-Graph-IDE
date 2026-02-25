using Microsoft.Extensions.Logging.Abstractions;
using SAGIDE.Core.Models;
using SAGIDE.Service.ActivityLogging;

namespace SAGIDE.Service.Tests;

/// <summary>
/// Unit tests for <see cref="MarkdownGenerator"/>.
/// Each test uses a unique temp directory so tests can run in parallel without interference.
/// </summary>
public class MarkdownGeneratorTests : IDisposable
{
    private readonly string _root;
    private readonly MarkdownGenerator _generator;

    public MarkdownGeneratorTests()
    {
        _root      = Path.Combine(Path.GetTempPath(), $"sagide-mdgen-test-{Guid.NewGuid():N}");
        _generator = new MarkdownGenerator(NullLogger<MarkdownGenerator>.Instance);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    /// <summary>
    /// <see cref="MarkdownGenerator.GenerateReadmeAsync"/> writes to <c>.sag-activity/README.md</c>
    /// but does NOT create the parent directory (unlike <c>GenerateHourlyLogAsync</c> which
    /// creates <c>.sag-activity/logs/</c>).  Call this before any Readme tests.
    /// </summary>
    private void EnsureSagActivityDir() =>
        Directory.CreateDirectory(Path.Combine(_root, ".sag-activity"));

    // ── GenerateHourlyLogAsync ─────────────────────────────────────────────────

    [Fact]
    public async Task GenerateHourlyLog_EmptyActivities_WritesPlaceholderLine()
    {
        await _generator.GenerateHourlyLogAsync(_root, "2026-02-24-14", [], CancellationToken.None);

        var logPath = Path.Combine(_root, ".sag-activity", "logs", "2026-02-24-14.md");
        Assert.True(File.Exists(logPath));

        var content = await File.ReadAllTextAsync(logPath);
        Assert.Contains("_No activities recorded in this hour._", content);
    }

    [Fact]
    public async Task GenerateHourlyLog_EmptyActivities_StillWritesHeader()
    {
        await _generator.GenerateHourlyLogAsync(_root, "2026-02-24-09", [], CancellationToken.None);

        var content = await File.ReadAllTextAsync(
            Path.Combine(_root, ".sag-activity", "logs", "2026-02-24-09.md"));

        Assert.Contains("# Activity Log: 2026-02-24-09", content);
        Assert.Contains("**Total Activities:** 0", content);
    }

    [Fact]
    public async Task GenerateHourlyLog_WithOneActivity_WritesSectionHeader()
    {
        var activities = new List<ActivityEntry>
        {
            new()
            {
                HourBucket   = "2026-02-24-10",
                Timestamp    = new DateTime(2026, 2, 24, 10, 15, 0, DateTimeKind.Utc),
                ActivityType = ActivityType.AgentTask,
                Actor        = "SAGIDE",
                Summary      = "Reviewed PR #42",
            }
        };

        await _generator.GenerateHourlyLogAsync(_root, "2026-02-24-10", activities, CancellationToken.None);

        var content = await File.ReadAllTextAsync(
            Path.Combine(_root, ".sag-activity", "logs", "2026-02-24-10.md"));

        // Section header for the group
        Assert.Contains("Agent Tasks", content);
        // Individual activity entry
        Assert.Contains("Reviewed PR #42", content);
        Assert.Contains("**Total Activities:** 1", content);
    }

    [Fact]
    public async Task GenerateHourlyLog_MultipleTypes_EachTypeHasOwnSection()
    {
        var activities = new List<ActivityEntry>
        {
            new()
            {
                Timestamp    = new DateTime(2026, 2, 24, 11, 0, 0, DateTimeKind.Utc),
                ActivityType = ActivityType.AgentTask,
                Actor        = "SAGIDE",
                Summary      = "Agent work",
            },
            new()
            {
                Timestamp    = new DateTime(2026, 2, 24, 11, 5, 0, DateTimeKind.Utc),
                ActivityType = ActivityType.GitCommit,
                Actor        = "user",
                Summary      = "git commit -m fix",
            }
        };

        await _generator.GenerateHourlyLogAsync(_root, "2026-02-24-11", activities, CancellationToken.None);

        var content = await File.ReadAllTextAsync(
            Path.Combine(_root, ".sag-activity", "logs", "2026-02-24-11.md"));

        Assert.Contains("Agent Tasks", content);
        Assert.Contains("Git Commits", content);
        Assert.Contains("**Total Activities:** 2", content);
    }

    [Fact]
    public async Task GenerateHourlyLog_WithTaskId_IncludesTaskIdLine()
    {
        var activities = new List<ActivityEntry>
        {
            new()
            {
                Timestamp    = new DateTime(2026, 2, 24, 12, 0, 0, DateTimeKind.Utc),
                ActivityType = ActivityType.AgentTask,
                Actor        = "SAGIDE",
                Summary      = "Task with ID",
                TaskId       = "abc123xyz",
            }
        };

        await _generator.GenerateHourlyLogAsync(_root, "2026-02-24-12", activities, CancellationToken.None);

        var content = await File.ReadAllTextAsync(
            Path.Combine(_root, ".sag-activity", "logs", "2026-02-24-12.md"));

        Assert.Contains("`abc123xyz`", content);
        Assert.Contains("**Task ID:**", content);
    }

    [Fact]
    public async Task GenerateHourlyLog_WithFilePaths_IncludesFilesSection()
    {
        var activities = new List<ActivityEntry>
        {
            new()
            {
                Timestamp    = new DateTime(2026, 2, 24, 13, 0, 0, DateTimeKind.Utc),
                ActivityType = ActivityType.FileModified,
                Actor        = "user",
                Summary      = "Edited files",
                FilePaths    = ["src/Foo.cs", "src/Bar.cs"],
            }
        };

        await _generator.GenerateHourlyLogAsync(_root, "2026-02-24-13", activities, CancellationToken.None);

        var content = await File.ReadAllTextAsync(
            Path.Combine(_root, ".sag-activity", "logs", "2026-02-24-13.md"));

        Assert.Contains("**Files:**", content);
        Assert.Contains("`src/Foo.cs`", content);
        Assert.Contains("`src/Bar.cs`", content);
    }

    [Fact]
    public async Task GenerateHourlyLog_WithDetails_IncludesCollapsibleSection()
    {
        var activities = new List<ActivityEntry>
        {
            new()
            {
                Timestamp    = new DateTime(2026, 2, 24, 14, 0, 0, DateTimeKind.Utc),
                ActivityType = ActivityType.SystemEvent,
                Actor        = "SAGIDE",
                Summary      = "System boot",
                Details      = """{"event":"startup"}""",
            }
        };

        await _generator.GenerateHourlyLogAsync(_root, "2026-02-24-14", activities, CancellationToken.None);

        var content = await File.ReadAllTextAsync(
            Path.Combine(_root, ".sag-activity", "logs", "2026-02-24-14.md"));

        Assert.Contains("<details>", content);
        Assert.Contains("<summary>Details</summary>", content);
        Assert.Contains("""{"event":"startup"}""", content);
    }

    [Fact]
    public async Task GenerateHourlyLog_CreatesLogsSubdirectory()
    {
        var logsDir = Path.Combine(_root, ".sag-activity", "logs");
        Assert.False(Directory.Exists(logsDir));

        await _generator.GenerateHourlyLogAsync(_root, "2026-02-24-15", [], CancellationToken.None);

        Assert.True(Directory.Exists(logsDir));
    }

    [Fact]
    public async Task GenerateHourlyLog_InvalidBucketFormat_Throws()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _generator.GenerateHourlyLogAsync(_root, "2026-02-24", [], CancellationToken.None));
    }

    // ── GenerateReadmeAsync ───────────────────────────────────────────────────

    [Fact]
    public async Task GenerateReadme_EmptyBuckets_WritesNoBucketsPlaceholder()
    {
        EnsureSagActivityDir();
        await _generator.GenerateReadmeAsync(_root, [], CancellationToken.None);

        var readmePath = Path.Combine(_root, ".sag-activity", "README.md");
        Assert.True(File.Exists(readmePath));

        var content = await File.ReadAllTextAsync(readmePath);
        Assert.Contains("No activity logs yet", content);
    }

    [Fact]
    public async Task GenerateReadme_WithOneBucket_WritesTableOfContents()
    {
        EnsureSagActivityDir();
        await _generator.GenerateReadmeAsync(_root, ["2026-02-24-10"], CancellationToken.None);

        var content = await File.ReadAllTextAsync(
            Path.Combine(_root, ".sag-activity", "README.md"));

        Assert.Contains("## Table of Contents", content);
        Assert.Contains("2026-02-24", content);
        Assert.Contains("logs/2026-02-24-10.md", content);
    }

    [Fact]
    public async Task GenerateReadme_MultipleDates_GroupsByDate()
    {
        var buckets = new List<string>
        {
            "2026-02-23-08",
            "2026-02-23-14",
            "2026-02-24-09",
        };

        EnsureSagActivityDir();
        await _generator.GenerateReadmeAsync(_root, buckets, CancellationToken.None);

        var content = await File.ReadAllTextAsync(
            Path.Combine(_root, ".sag-activity", "README.md"));

        // Both dates should appear as section headers
        Assert.Contains("2026-02-24", content);
        Assert.Contains("2026-02-23", content);
        // Both per-day hours should be linked
        Assert.Contains("logs/2026-02-23-08.md", content);
        Assert.Contains("logs/2026-02-23-14.md", content);
        Assert.Contains("logs/2026-02-24-09.md", content);
    }

    [Fact]
    public async Task GenerateReadme_WithBuckets_WritesGeneratedByFooter()
    {
        EnsureSagActivityDir();
        await _generator.GenerateReadmeAsync(_root, ["2026-02-24-16"], CancellationToken.None);

        var content = await File.ReadAllTextAsync(
            Path.Combine(_root, ".sag-activity", "README.md"));

        Assert.Contains("Generated by SAG IDE Activity Logger", content);
    }

    [Fact]
    public async Task GenerateReadme_ContainsTitle()
    {
        EnsureSagActivityDir();
        await _generator.GenerateReadmeAsync(_root, [], CancellationToken.None);

        var content = await File.ReadAllTextAsync(
            Path.Combine(_root, ".sag-activity", "README.md"));

        Assert.Contains("# SAG IDE Activity Log", content);
    }
}
