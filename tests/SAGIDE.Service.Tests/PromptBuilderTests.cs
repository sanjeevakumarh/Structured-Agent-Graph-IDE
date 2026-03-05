using SAGIDE.Core.Models;
using SAGIDE.Service.Providers;

namespace SAGIDE.Service.Tests;

public class PromptBuilderTests : IDisposable
{
    private readonly string _tmpDir;

    public PromptBuilderTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), $"pb-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tmpDir);
    }

    public void Dispose() => Directory.Delete(_tmpDir, recursive: true);

    private string WriteFile(string name, string content)
    {
        var path = Path.Combine(_tmpDir, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public async Task EmptyFileList_ReturnsDescriptionOnly()
    {
        var builder = new PromptBuilder();
        var task    = new AgentTask { AgentType = AgentType.Generic, Description = "Do something" };

        var prompt = await builder.BuildAsync(task);

        Assert.Equal("Do something", prompt);
    }

    [Fact]
    public async Task NullDescription_ReturnsEmptyStringForGeneric()
    {
        var builder = new PromptBuilder();
        var task    = new AgentTask { AgentType = AgentType.Generic, Description = null! };

        var prompt = await builder.BuildAsync(task);

        Assert.Equal(string.Empty, prompt);
    }

    [Fact]
    public async Task SingleFile_ContentIncludedInPrompt()
    {
        var path    = WriteFile("hello.cs", "class Hello {}");
        var builder = new PromptBuilder();
        var task    = new AgentTask
        {
            AgentType   = AgentType.CodeReview,
            Description = "Review this",
            FilePaths   = [path],
        };

        var prompt = await builder.BuildAsync(task);

        Assert.Contains("hello.cs", prompt);
        Assert.Contains("class Hello {}", prompt);
    }

    [Fact]
    public async Task NonExistentFile_SkippedWithoutException()
    {
        var builder = new PromptBuilder();
        var task    = new AgentTask
        {
            AgentType   = AgentType.Generic,
            Description = "Review",
            FilePaths   = ["/nonexistent/path/file.cs"],
        };

        // Should not throw
        var prompt = await builder.BuildAsync(task);
        Assert.Equal("Review", prompt);
    }

    [Fact]
    public async Task OversizedFile_Truncated()
    {
        var bigContent = new string('x', 10_000);
        var path       = WriteFile("big.cs", bigContent);
        var builder    = new PromptBuilder(maxFileSizeChars: 500);

        var task = new AgentTask
        {
            AgentType   = AgentType.CodeReview,
            Description = "Check",
            FilePaths   = [path],
        };

        var prompt = await builder.BuildAsync(task);

        Assert.Contains("truncated", prompt);
        Assert.DoesNotContain(bigContent, prompt);
    }

    [Fact]
    public async Task DuplicateFilePaths_DoesNotThrow()
    {
        var path = WriteFile("dup.cs", "code here");

        // The file cache prevents multiple disk reads, but each FilePaths entry
        // does produce an output section — verify no exception is thrown.
        var builder = new PromptBuilder();
        var task    = new AgentTask
        {
            AgentType   = AgentType.CodeReview,
            Description = "x",
            FilePaths   = [path, path, path],
        };
        var prompt = await builder.BuildAsync(task);
        Assert.Contains("dup.cs", prompt);
    }

    [Fact]
    public async Task CodeReview_IncludesRolePrefix()
    {
        var builder = new PromptBuilder();
        var task    = new AgentTask
        {
            AgentType   = AgentType.CodeReview,
            Description = "Check this code",
        };

        var prompt = await builder.BuildAsync(task);

        Assert.StartsWith("You are a code review agent.", prompt);
        Assert.Contains("Check this code", prompt);
    }

    [Fact]
    public async Task SecurityReview_IncludesRolePrefix()
    {
        var builder = new PromptBuilder();
        var task    = new AgentTask
        {
            AgentType   = AgentType.SecurityReview,
            Description = "Scan for vulns",
        };

        var prompt = await builder.BuildAsync(task);

        Assert.StartsWith("You are a security review agent.", prompt);
        Assert.Contains("OWASP", prompt);
    }

    [Fact]
    public async Task Refactoring_IncludesRolePrefix()
    {
        var builder = new PromptBuilder();
        var task    = new AgentTask
        {
            AgentType   = AgentType.Refactoring,
            Description = "Improve this",
        };

        var prompt = await builder.BuildAsync(task);

        Assert.StartsWith("You are a refactoring agent.", prompt);
    }

    private static int CountOccurrences(string text, string pattern)
    {
        int count = 0, idx = 0;
        while ((idx = text.IndexOf(pattern, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += pattern.Length;
        }
        return count;
    }
}
