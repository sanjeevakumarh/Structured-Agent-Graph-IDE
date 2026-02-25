using SAGIDE.Core.Models;
using SAGIDE.Service.Orchestrator;
using SAGIDE.Service.Resilience;
using SAGIDE.Service.Agents;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace SAGIDE.Service.Tests;

public class TaskQueueTests
{
    [Fact]
    public void Enqueue_Dequeue_ReturnsTask()
    {
        var queue = new TaskQueue();
        var task = new AgentTask { AgentType = AgentType.CodeReview, Description = "Test" };

        queue.Enqueue(task);
        var dequeued = queue.Dequeue();

        Assert.NotNull(dequeued);
        Assert.Equal(task.Id, dequeued.Id);
        Assert.Equal(AgentTaskStatus.Running, dequeued.Status);
    }

    [Fact]
    public void Dequeue_EmptyQueue_ReturnsNull()
    {
        var queue = new TaskQueue();
        Assert.Null(queue.Dequeue());
    }

    [Fact]
    public void GetTask_ExistingId_ReturnsTask()
    {
        var queue = new TaskQueue();
        var task = new AgentTask { Description = "Test" };
        queue.Enqueue(task);

        var found = queue.GetTask(task.Id);
        Assert.NotNull(found);
        Assert.Equal(task.Id, found.Id);
    }

    [Fact]
    public void GetTask_NonExistentId_ReturnsNull()
    {
        var queue = new TaskQueue();
        Assert.Null(queue.GetTask("nonexistent"));
    }

    [Fact]
    public void PriorityOrder_HigherPriorityFirst()
    {
        var queue = new TaskQueue();
        var low = new AgentTask { Description = "Low", Priority = 0 };
        var high = new AgentTask { Description = "High", Priority = 10 };

        queue.Enqueue(low);
        queue.Enqueue(high);

        var first = queue.Dequeue();
        Assert.Equal("High", first!.Description);
    }
}

public class RetryPolicyTests
{
    [Fact]
    public void ExponentialBackoff_DoublesDelay()
    {
        var policy = new RetryPolicy
        {
            Strategy = BackoffStrategy.Exponential,
            InitialDelay = TimeSpan.FromSeconds(1)
        };

        Assert.Equal(TimeSpan.FromSeconds(1), policy.GetDelay(0));
        Assert.Equal(TimeSpan.FromSeconds(2), policy.GetDelay(1));
        Assert.Equal(TimeSpan.FromSeconds(4), policy.GetDelay(2));
    }

    [Fact]
    public void FixedBackoff_SameDelay()
    {
        var policy = new RetryPolicy
        {
            Strategy = BackoffStrategy.Fixed,
            InitialDelay = TimeSpan.FromSeconds(2)
        };

        Assert.Equal(TimeSpan.FromSeconds(2), policy.GetDelay(0));
        Assert.Equal(TimeSpan.FromSeconds(2), policy.GetDelay(1));
        Assert.Equal(TimeSpan.FromSeconds(2), policy.GetDelay(2));
    }

    [Fact]
    public void IsRetryable_429_ReturnsTrue()
    {
        var policy = RetryPolicy.Default;
        Assert.True(policy.IsRetryable(429));
        Assert.True(policy.IsRetryable(500));
        Assert.True(policy.IsRetryable(503));
    }

    [Fact]
    public void IsRetryable_400_ReturnsFalse()
    {
        var policy = RetryPolicy.Default;
        Assert.False(policy.IsRetryable(400));
        Assert.False(policy.IsRetryable(401));
        Assert.False(policy.IsRetryable(404));
    }
}

public class DeadLetterQueueTests
{
    private DeadLetterQueue CreateDlq() =>
        new(NullLogger<DeadLetterQueue>.Instance);

    [Fact]
    public void Enqueue_IncreasesCount()
    {
        var dlq = CreateDlq();
        var task = new AgentTask { Description = "Test", AgentType = AgentType.Debug };

        dlq.Enqueue(task, "Some error");

        Assert.Equal(1, dlq.Count);
    }

    [Fact]
    public void DequeueForRetry_RemovesEntry()
    {
        var dlq = CreateDlq();
        var task = new AgentTask { Description = "Test" };
        dlq.Enqueue(task, "err");

        var entries = dlq.GetAll();
        var entry = dlq.DequeueForRetry(entries[0].Id);

        Assert.NotNull(entry);
        Assert.Equal(0, dlq.Count);
    }

    [Fact]
    public void Discard_RemovesEntry()
    {
        var dlq = CreateDlq();
        var task = new AgentTask { Description = "Test" };
        dlq.Enqueue(task, "err");

        var entries = dlq.GetAll();
        Assert.True(dlq.Discard(entries[0].Id));
        Assert.Equal(0, dlq.Count);
    }

    [Fact]
    public void DequeueForRetry_NonExistent_ReturnsNull()
    {
        var dlq = CreateDlq();
        Assert.Null(dlq.DequeueForRetry("doesnotexist"));
    }
}

public class ResultParserTests
{
    private ResultParser CreateParser() =>
        new(NullLogger<ResultParser>.Instance);

    [Fact]
    public void Parse_JsonBlock_ExtractsIssues()
    {
        var parser = CreateParser();
        var rawOutput = """
            Here are the issues I found:

            ```json
            {
              "issues": [
                {
                  "filePath": "src/Foo.cs",
                  "line": 42,
                  "severity": "high",
                  "message": "SQL injection vulnerability",
                  "suggestedFix": "Use parameterized query"
                }
              ]
            }
            ```
            """;

        var result = parser.Parse("task1", AgentType.CodeReview, rawOutput, 1000);

        Assert.True(result.Success);
        Assert.Single(result.Issues);
        Assert.Equal("src/Foo.cs", result.Issues[0].FilePath);
        Assert.Equal(42, result.Issues[0].Line);
        Assert.Equal(IssueSeverity.High, result.Issues[0].Severity);
        Assert.Contains("SQL injection", result.Issues[0].Message);
    }

    [Fact]
    public void Parse_NoJson_ReturnsRawOutput()
    {
        var parser = CreateParser();
        var rawOutput = "This is just a plain text response with no structured data.";

        var result = parser.Parse("task2", AgentType.Documentation, rawOutput, 500);

        Assert.True(result.Success);
        Assert.Equal(rawOutput, result.Output);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void Parse_CodeBlocks_ExtractsChanges()
    {
        var parser = CreateParser();
        var rawOutput = """
            Here are the generated tests:

            ```csharp
            [Fact]
            public void Test_Add() { Assert.Equal(3, Add(1, 2)); }
            ```
            """;

        var result = parser.Parse("task3", AgentType.TestGeneration, rawOutput, 800);

        Assert.True(result.Success);
        Assert.Single(result.Changes);
        Assert.Contains("Assert.Equal", result.Changes[0].NewContent);
    }
}

public class TimeoutConfigTests
{
    [Fact]
    public void GetProviderTimeoutMs_KnownProvider_ReturnsConfigured()
    {
        var config = new TimeoutConfig();
        Assert.Equal(7_200_000, config.GetProviderTimeoutMs(ModelProvider.Claude));
    }

    [Fact]
    public void GetProviderTimeoutMs_UnknownProvider_ReturnsDefault()
    {
        var config = new TimeoutConfig();
        // Ollama is configured with 7200s (2h) — generous to support long-running local models
        Assert.Equal(7_200_000, config.GetProviderTimeoutMs(ModelProvider.Ollama));
    }

    [Fact]
    public void TaskExecutionTimeout_ReturnsTimeSpan()
    {
        var config = new TimeoutConfig { TaskExecutionMs = 60_000 };
        Assert.Equal(TimeSpan.FromMinutes(1), config.TaskExecutionTimeout);
    }
}

/// <summary>
/// Requirement: No environment-specific identifiers (real hostnames, usernames, local paths)
/// should appear in any shared artifact — prompt YAMLs, docs, source code, or planning docs.
///
/// All machine references in prompt YAMLs must use the logical alias names defined in
/// appsettings.json under SAGIDE:Ollama:Servers[].Name and SAGIDE:OpenAICompatible:Servers[].Name.
/// Real hostnames belong only inside appsettings.json (which is gitignored / kept private).
///
/// Approved aliases: localhost, workstation, mini
/// When you add a new machine, add its alias here AND in appsettings.json — never use the real hostname elsewhere.
/// </summary>
public class EnvironmentLeakTests
{
    // Logical aliases defined in appsettings.json — the ONLY names permitted in @machine notation
    private static readonly HashSet<string> ApprovedAliases =
    [
    ];

    // Real hostnames / usernames that must never appear in shared files
    private static readonly string[] ForbiddenPatterns =
    [
    ];

    // Directories that are shared (scanned for leaks)
    private static readonly string[] SharedDirs = ["prompts", "docs", "src", "tools"];

    // Extensions to scan in shared dirs
    private static readonly string[] ScannedExtensions = [".yaml", ".cs", ".ts", ".md", ".json"];

    // Files that are explicitly environment-specific and excluded from the scan
    private static readonly string[] ExcludedFiles =
    [
        "appsettings.json",           // authoritative env config — real hostnames live here
        "appsettings.Development.json",
        "appsettings.Production.json",
    ];

    private static string RepoRoot()
    {
        // Walk up from the test assembly location until we find the .git directory
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, ".git")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Could not locate repo root (no .git directory found).");
    }

    [Fact]
    public void SharedFiles_ContainNoForbiddenHostnames()
    {
        var root = RepoRoot();
        var violations = new List<string>();

        foreach (var sharedDir in SharedDirs)
        {
            var dirPath = Path.Combine(root, sharedDir);
            if (!Directory.Exists(dirPath)) continue;

            foreach (var file in Directory.EnumerateFiles(dirPath, "*", SearchOption.AllDirectories))
            {
                if (!ScannedExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                    continue;
                if (ExcludedFiles.Contains(Path.GetFileName(file), StringComparer.OrdinalIgnoreCase))
                    continue;

                var lines = File.ReadAllLines(file);
                for (var i = 0; i < lines.Length; i++)
                {
                    foreach (var forbidden in ForbiddenPatterns)
                    {
                        // Case-sensitive: "jetson" is the hostname; "Jetson" is the NVIDIA chipset brand
                        if (lines[i].Contains(forbidden, StringComparison.Ordinal))
                            violations.Add($"{Path.GetRelativePath(root, file)}:{i + 1} — contains '{forbidden}'");
                    }
                }
            }
        }

        Assert.True(violations.Count == 0,
            "Environment-specific identifiers found in shared files:\n" + string.Join("\n", violations));
    }

    [Fact]
    public void PromptYamls_MachineNames_AreApprovedAliases()
    {
        var root = RepoRoot();
        var promptsDir = Path.Combine(root, "prompts");
        if (!Directory.Exists(promptsDir)) return;

        var violations = new List<string>();
        var atMachine = new System.Text.RegularExpressions.Regex(@"@([\w][\w\-]*)");

        foreach (var file in Directory.EnumerateFiles(promptsDir, "*.yaml", SearchOption.AllDirectories))
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                foreach (System.Text.RegularExpressions.Match m in atMachine.Matches(lines[i]))
                {
                    var alias = m.Groups[1].Value;
                    if (!ApprovedAliases.Contains(alias))
                        violations.Add($"{Path.GetRelativePath(root, file)}:{i + 1} — unapproved machine alias '@{alias}' (add to ApprovedAliases and appsettings.json)");
                }
            }
        }

        Assert.True(violations.Count == 0,
            "Prompt YAMLs reference unapproved machine aliases:\n" + string.Join("\n", violations));
    }
}

public class AgentLimitsConfigTests
{
    [Fact]
    public void GetMaxIterations_KnownAgent_ReturnsConfigured()
    {
        var config = new AgentLimitsConfig();
        Assert.Equal(5, config.GetMaxIterations(AgentType.Refactoring));
    }

    [Fact]
    public void GetMaxIterations_UnknownAgent_ReturnsDefault()
    {
        var config = new AgentLimitsConfig();
        // Default fallback is 5
        Assert.Equal(5, config.GetMaxIterations((AgentType)999));
    }
}
