using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SAGIDE.Core.Models;
using SAGIDE.Service.Orchestrator;

namespace SAGIDE.Service.Tests;

/// <summary>
/// Unit tests for <see cref="WorkflowDefinitionLoader"/> covering:
/// - <see cref="WorkflowDefinitionLoader.MapAgentName"/> known-alias mapping
/// - <see cref="WorkflowDefinitionLoader.ParseYaml"/> happy-path field mapping
/// - Validation: unknown depends_on, cycles, missing convergence_policy on loops
/// - <see cref="WorkflowDefinitionLoader.GetBuiltInDefinitions"/> missing directory
/// - <see cref="WorkflowDefinitionLoader.LoadFromWorkspace"/> temp directory round-trip
/// </summary>
public class WorkflowDefinitionLoaderTests
{
    private static WorkflowDefinitionLoader MakeLoader(string? templatesDir = null)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(templatesDir is not null
                ? new Dictionary<string, string?> { ["SAGIDE:BuiltInTemplatesPath"] = templatesDir }
                : [])
            .Build();
        return new WorkflowDefinitionLoader(
            NullLogger<WorkflowDefinitionLoader>.Instance, config);
    }

    // ── MapAgentName ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("coder",        AgentType.Refactoring)]
    [InlineData("codegenerator",AgentType.Refactoring)]
    [InlineData("generator",    AgentType.Refactoring)]
    [InlineData("reviewer",     AgentType.CodeReview)]
    [InlineData("codereviewer", AgentType.CodeReview)]
    [InlineData("codereview",   AgentType.CodeReview)]
    [InlineData("tester",       AgentType.TestGeneration)]
    [InlineData("unittester",   AgentType.TestGeneration)]
    [InlineData("security",     AgentType.SecurityReview)]
    [InlineData("documenter",   AgentType.Documentation)]
    [InlineData("debug",        AgentType.Debug)]
    [InlineData("refactoring",  AgentType.Refactoring)]
    [InlineData("unknown_xyz",  AgentType.CodeReview)]   // fallthrough default
    public void MapAgentName_KnownAliases(string name, AgentType expected)
    {
        Assert.Equal(expected, WorkflowDefinitionLoader.MapAgentName(name));
    }

    // ── ParseYaml — happy path ────────────────────────────────────────────────

    private const string MinimalYaml = """
        name: My Workflow
        steps:
          - id: review
            type: agent
            agent: reviewer
        """;

    [Fact]
    public void ParseYaml_Minimal_FieldsMapped()
    {
        var loader = MakeLoader();
        var def    = loader.ParseYaml(MinimalYaml, "fallback-id");

        Assert.Equal("My Workflow", def.Name);
        Assert.Equal("fallback-id", def.Id);   // id not in YAML → fallback
        Assert.Single(def.Steps);
        Assert.Equal("review", def.Steps[0].Id);
        Assert.Equal("agent",  def.Steps[0].Type);
        Assert.Equal("reviewer", def.Steps[0].Agent);
    }

    private const string FullStepYaml = """
        id: wf-full
        name: Full Workflow
        steps:
          - id: step1
            type: agent
            agent: coder
            model_id: deepseek-coder:6.7b
            max_iterations: 3
            timeout_sec: 120
          - id: step2
            type: agent
            agent: reviewer
            depends_on: [step1]
        """;

    [Fact]
    public void ParseYaml_StepFields_MappedCorrectly()
    {
        var loader = MakeLoader();
        var def    = loader.ParseYaml(FullStepYaml, "ignored");

        Assert.Equal("wf-full", def.Id);
        Assert.Equal("Full Workflow", def.Name);
        Assert.Equal(2, def.Steps.Count);

        var s1 = def.Steps[0];
        Assert.Equal("step1",               s1.Id);
        Assert.Equal("deepseek-coder:6.7b", s1.ModelId);
        Assert.Equal(3,                     s1.MaxIterations);
        Assert.Equal(120,                   s1.TimeoutSec);

        var s2 = def.Steps[1];
        Assert.Equal(["step1"],             s2.DependsOn);
    }

    private const string ConvergenceYaml = """
        id: loop-wf
        name: Loop Workflow
        convergence_policy:
          max_iterations: 5
          escalation_target: HUMAN_APPROVAL
          partial_retry_scope: FAILING_NODES_ONLY
          contradiction_detection: true
        steps:
          - id: coder
            type: agent
            agent: coder
            next: reviewer
          - id: reviewer
            type: agent
            agent: reviewer
        """;

    [Fact]
    public void ParseYaml_ConvergencePolicy_FieldsMapped()
    {
        var loader = MakeLoader();
        var def    = loader.ParseYaml(ConvergenceYaml, "id");

        Assert.NotNull(def.ConvergencePolicy);
        Assert.Equal(5,                  def.ConvergencePolicy.MaxIterations);
        Assert.Equal("HUMAN_APPROVAL",   def.ConvergencePolicy.EscalationTarget);
        Assert.Equal("FAILING_NODES_ONLY", def.ConvergencePolicy.PartialRetryScope);
        Assert.True(def.ConvergencePolicy.ContradictionDetection);
    }

    private const string RouterYaml = """
        id: router-wf
        name: Router Workflow
        steps:
          - id: route
            type: router
            branches:
              - condition: "issue_count() > 0"
                target: fix
              - condition: "true"
                target: done
          - id: fix
            type: agent
            agent: coder
          - id: done
            type: agent
            agent: reviewer
        """;

    [Fact]
    public void ParseYaml_RouterBranches_MappedCorrectly()
    {
        var loader = MakeLoader();
        var def    = loader.ParseYaml(RouterYaml, "id");

        var route = def.Steps[0];
        Assert.NotNull(route.Router);
        Assert.Equal(2, route.Router.Branches.Count);
        Assert.Equal("issue_count() > 0", route.Router.Branches[0].Condition);
        Assert.Equal("fix",               route.Router.Branches[0].Target);
    }

    // ── Validation ────────────────────────────────────────────────────────────

    [Fact]
    public void ParseYaml_UnknownDependsOn_ThrowsInvalidOperation()
    {
        var yaml = """
            name: Bad Deps
            steps:
              - id: step1
                type: agent
                agent: coder
                depends_on: [ghost_step]
            """;

        var loader = MakeLoader();
        var ex     = Assert.Throws<InvalidOperationException>(() => loader.ParseYaml(yaml, "id"));
        Assert.Contains("ghost_step", ex.Message);
    }

    [Fact]
    public void ParseYaml_CycleInDependsOn_ThrowsInvalidOperation()
    {
        var yaml = """
            name: Cycle
            steps:
              - id: a
                type: agent
                agent: coder
                depends_on: [b]
              - id: b
                type: agent
                agent: reviewer
                depends_on: [a]
            """;

        var loader = MakeLoader();
        var ex     = Assert.Throws<InvalidOperationException>(() => loader.ParseYaml(yaml, "id"));
        Assert.Contains("Circular", ex.Message);
    }

    [Fact]
    public void ParseYaml_LoopWithoutConvergencePolicy_ThrowsInvalidOperation()
    {
        var yaml = """
            name: Loop No Policy
            steps:
              - id: a
                type: agent
                agent: coder
                next: b
              - id: b
                type: agent
                agent: reviewer
            """;

        var loader = MakeLoader();
        var ex     = Assert.Throws<InvalidOperationException>(() => loader.ParseYaml(yaml, "id"));
        Assert.Contains("convergence_policy", ex.Message);
    }

    [Fact]
    public void ParseYaml_InvalidEscalationTarget_ThrowsInvalidOperation()
    {
        var yaml = """
            name: Bad Escalation
            convergence_policy:
              max_iterations: 3
              escalation_target: UNKNOWN_TARGET
              partial_retry_scope: FAILING_NODES_ONLY
            steps:
              - id: a
                type: agent
                agent: coder
                next: a
            """;

        var loader = MakeLoader();
        var ex     = Assert.Throws<InvalidOperationException>(() => loader.ParseYaml(yaml, "id"));
        Assert.Contains("escalation_target", ex.Message);
    }

    [Fact]
    public void ParseYaml_ToolStepMissingCommand_ThrowsInvalidOperation()
    {
        var yaml = """
            name: No Command
            steps:
              - id: tool1
                type: tool
            """;

        var loader = MakeLoader();
        var ex     = Assert.Throws<InvalidOperationException>(() => loader.ParseYaml(yaml, "id"));
        Assert.Contains("command", ex.Message);
    }

    // ── Directory loading ─────────────────────────────────────────────────────

    [Fact]
    public void GetBuiltInDefinitions_MissingDir_ReturnsEmpty()
    {
        var loader = MakeLoader("/no/such/dir/templates");
        var result = loader.GetBuiltInDefinitions();
        Assert.Empty(result);
    }

    [Fact]
    public void LoadFromWorkspace_MissingDir_ReturnsEmpty()
    {
        var loader = MakeLoader();
        var result = loader.LoadFromWorkspace("/workspace/that/does/not/exist");
        Assert.Empty(result);
    }

    [Fact]
    public void LoadFromWorkspace_ValidYaml_LoadsDefinition()
    {
        var workspace = Path.Combine(Path.GetTempPath(), $"wf-test-{Guid.NewGuid():N}");
        var dir       = Path.Combine(workspace, ".agentide", "workflows");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "my-review.yaml"), MinimalYaml);

        try
        {
            var loader = MakeLoader();
            var defs   = loader.LoadFromWorkspace(workspace);

            Assert.Single(defs);
            Assert.Equal("My Workflow", defs[0].Name);
            Assert.False(defs[0].IsBuiltIn);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public void LoadFromWorkspace_InvalidYaml_SkipsFile_ReturnsOthers()
    {
        var workspace = Path.Combine(Path.GetTempPath(), $"wf-test-{Guid.NewGuid():N}");
        var dir       = Path.Combine(workspace, ".agentide", "workflows");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "bad.yaml"), ": ::: invalid yaml ::");
        File.WriteAllText(Path.Combine(dir, "good.yaml"), MinimalYaml);

        try
        {
            var loader = MakeLoader();
            var defs   = loader.LoadFromWorkspace(workspace);

            // bad.yaml skipped; good.yaml loaded
            Assert.Single(defs);
            Assert.Equal("My Workflow", defs[0].Name);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }
}
