using System.Text.Json;
using SAGIDE.Core.DTOs;
using SAGIDE.Core.Models;

namespace SAGIDE.Service.Tests;

/// <summary>
/// Verifies JSON round-trip fidelity for all REST API request/response DTOs.
/// Uses System.Text.Json (the same serializer as ASP.NET Core minimal APIs).
/// </summary>
public class DtoSerializationTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    // ── SubmitTaskRequest ──────────────────────────────────────────────────────

    [Fact]
    public void SubmitTaskRequest_RoundTrip_PreservesAllFields()
    {
        var original = new SubmitTaskRequest
        {
            AgentType         = AgentType.CodeReview,
            ModelProvider     = ModelProvider.Claude,
            ModelId           = "claude-3-5-sonnet",
            Description       = "Review the PR",
            FilePaths         = ["src/Foo.cs", "src/Bar.cs"],
            Priority          = 5,
            Metadata          = new Dictionary<string, string> { ["key"] = "value" },
            ScheduledFor      = new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc),
            ComparisonGroupId = "group-abc",
            ModelEndpoint     = "http://workstation:11434",
            SourceTag         = "cli",
        };

        var json        = JsonSerializer.Serialize(original, Options);
        var deserialized = JsonSerializer.Deserialize<SubmitTaskRequest>(json, Options)!;

        Assert.Equal(original.AgentType,         deserialized.AgentType);
        Assert.Equal(original.ModelProvider,     deserialized.ModelProvider);
        Assert.Equal(original.ModelId,           deserialized.ModelId);
        Assert.Equal(original.Description,       deserialized.Description);
        Assert.Equal(original.FilePaths,         deserialized.FilePaths);
        Assert.Equal(original.Priority,          deserialized.Priority);
        Assert.Equal(original.Metadata,          deserialized.Metadata);
        Assert.Equal(original.ScheduledFor,      deserialized.ScheduledFor);
        Assert.Equal(original.ComparisonGroupId, deserialized.ComparisonGroupId);
        Assert.Equal(original.ModelEndpoint,     deserialized.ModelEndpoint);
        Assert.Equal(original.SourceTag,         deserialized.SourceTag);
    }

    [Fact]
    public void SubmitTaskRequest_Defaults_AreSafeToDeserializeFromEmptyObject()
    {
        var dto = JsonSerializer.Deserialize<SubmitTaskRequest>("{}", Options)!;

        Assert.Equal(AgentType.CodeReview,  dto.AgentType);   // enum default = 0
        Assert.Equal(ModelProvider.Claude,  dto.ModelProvider); // enum default = 0
        Assert.Equal(string.Empty,          dto.ModelId);
        Assert.Equal(string.Empty,          dto.Description);
        Assert.Empty(dto.FilePaths);
        Assert.Empty(dto.Metadata);
        Assert.Null(dto.ScheduledFor);
        Assert.Null(dto.SourceTag);
        Assert.Null(dto.ModelEndpoint);
        Assert.Null(dto.ComparisonGroupId);
    }

    [Fact]
    public void SubmitTaskRequest_UnknownFields_AreIgnored()
    {
        const string json = """{"description":"test","unknownField":"should-be-dropped","agentType":0}""";

        var dto = JsonSerializer.Deserialize<SubmitTaskRequest>(json, Options)!;

        Assert.Equal("test", dto.Description);
        Assert.Equal(AgentType.CodeReview, dto.AgentType);
    }

    [Fact]
    public void SubmitTaskRequest_NullableSourceTag_SurvivesNullAndString()
    {
        var withNull   = JsonSerializer.Deserialize<SubmitTaskRequest>("""{"description":"t","sourceTag":null}""", Options)!;
        var withString = JsonSerializer.Deserialize<SubmitTaskRequest>("""{"description":"t","sourceTag":"finance"}""", Options)!;

        Assert.Null(withNull.SourceTag);
        Assert.Equal("finance", withString.SourceTag);
    }

    // ── TaskStatusResponse ─────────────────────────────────────────────────────

    [Fact]
    public void TaskStatusResponse_RoundTrip_PreservesAllFields()
    {
        var original = new TaskStatusResponse
        {
            TaskId         = "abc123",
            Status         = AgentTaskStatus.Running,
            Progress       = 42,
            StatusMessage  = "in progress",
            AgentType      = AgentType.TestGeneration,
            ModelProvider  = ModelProvider.Claude,
            ModelId        = "claude-3-5-haiku",
            CreatedAt      = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            StartedAt      = new DateTime(2026, 1, 1, 0, 1, 0, DateTimeKind.Utc),
            CompletedAt    = null,
            ScheduledFor   = null,
            ComparisonGroupId = null,
            Result         = null,
        };

        var json        = JsonSerializer.Serialize(original, Options);
        var deserialized = JsonSerializer.Deserialize<TaskStatusResponse>(json, Options)!;

        Assert.Equal(original.TaskId,        deserialized.TaskId);
        Assert.Equal(original.Status,        deserialized.Status);
        Assert.Equal(original.Progress,      deserialized.Progress);
        Assert.Equal(original.StatusMessage, deserialized.StatusMessage);
        Assert.Equal(original.AgentType,     deserialized.AgentType);
        Assert.Equal(original.ModelProvider, deserialized.ModelProvider);
        Assert.Equal(original.ModelId,       deserialized.ModelId);
        Assert.Equal(original.CreatedAt,     deserialized.CreatedAt);
        Assert.Equal(original.StartedAt,     deserialized.StartedAt);
        Assert.Null(deserialized.CompletedAt);
        Assert.Null(deserialized.Result);
    }

    [Fact]
    public void TaskStatusResponse_AllStatusValues_Serialize()
    {
        foreach (AgentTaskStatus status in Enum.GetValues<AgentTaskStatus>())
        {
            var dto  = new TaskStatusResponse { Status = status };
            var json = JsonSerializer.Serialize(dto, Options);
            var back = JsonSerializer.Deserialize<TaskStatusResponse>(json, Options)!;
            Assert.Equal(status, back.Status);
        }
    }

    // ── WorkflowRequests ──────────────────────────────────────────────────────

    [Fact]
    public void StartWorkflowRequest_RoundTrip_PreservesInputsAndOverrides()
    {
        var original = new StartWorkflowRequest
        {
            DefinitionId          = "wf-code-review",
            Inputs                = new Dictionary<string, string> { ["branch"] = "main" },
            FilePaths             = ["src/Main.cs"],
            DefaultModelId        = "qwen2.5-coder",
            DefaultModelProvider  = "Ollama",
            ModelEndpoint         = "http://workstation:11434",
            WorkspacePath         = "/home/user/project",
            StepModelOverrides    = new Dictionary<string, StepModelOverride>
            {
                ["step1"] = new StepModelOverride { Provider = "Ollama", ModelId = "llama3", Endpoint = null }
            },
        };

        var json        = JsonSerializer.Serialize(original, Options);
        var deserialized = JsonSerializer.Deserialize<StartWorkflowRequest>(json, Options)!;

        Assert.Equal(original.DefinitionId,      deserialized.DefinitionId);
        Assert.Equal(original.Inputs,            deserialized.Inputs);
        Assert.Equal(original.FilePaths,         deserialized.FilePaths);
        Assert.Equal(original.DefaultModelId,    deserialized.DefaultModelId);
        Assert.Equal(original.DefaultModelProvider, deserialized.DefaultModelProvider);
        Assert.Equal(original.ModelEndpoint,     deserialized.ModelEndpoint);
        Assert.Equal(original.WorkspacePath,     deserialized.WorkspacePath);
        Assert.Single(deserialized.StepModelOverrides);
        Assert.Equal("llama3", deserialized.StepModelOverrides["step1"].ModelId);
    }

    [Fact]
    public void ApproveWorkflowStepRequest_RoundTrip_PreservesApprovedFlag()
    {
        var approve = new ApproveWorkflowStepRequest
        {
            InstanceId = "inst-001",
            StepId     = "human_gate",
            Approved   = true,
            Comment    = "Looks good",
        };
        var reject = new ApproveWorkflowStepRequest
        {
            InstanceId = "inst-001",
            StepId     = "human_gate",
            Approved   = false,
            Comment    = "Needs rework",
        };

        var jsonApprove = JsonSerializer.Serialize(approve, Options);
        var jsonReject  = JsonSerializer.Serialize(reject, Options);

        var backApprove = JsonSerializer.Deserialize<ApproveWorkflowStepRequest>(jsonApprove, Options)!;
        var backReject  = JsonSerializer.Deserialize<ApproveWorkflowStepRequest>(jsonReject, Options)!;

        Assert.True(backApprove.Approved);
        Assert.Equal("Looks good", backApprove.Comment);
        Assert.False(backReject.Approved);
        Assert.Equal("Needs rework", backReject.Comment);
    }

    [Fact]
    public void WorkflowStartedResponse_RoundTrip()
    {
        var original = new WorkflowStartedResponse { InstanceId = "abc-001" };

        var json = JsonSerializer.Serialize(original, Options);
        var back = JsonSerializer.Deserialize<WorkflowStartedResponse>(json, Options)!;

        Assert.Equal("abc-001", back.InstanceId);
    }

    // ── Enum coverage ─────────────────────────────────────────────────────────

    [Fact]
    public void AgentType_AllValues_SerializeAsExpectedIntegers()
    {
        // Ensures enum integer values haven't accidentally shifted
        Assert.Equal(0, (int)AgentType.CodeReview);
        Assert.Equal(1, (int)AgentType.TestGeneration);
        Assert.Equal(2, (int)AgentType.Refactoring);
        Assert.Equal(3, (int)AgentType.Debug);
        Assert.Equal(4, (int)AgentType.Documentation);
        Assert.Equal(5, (int)AgentType.SecurityReview);
        Assert.Equal(6, (int)AgentType.Generic);
    }

    [Fact]
    public void ModelProvider_AllValues_Serialize()
    {
        foreach (ModelProvider provider in Enum.GetValues<ModelProvider>())
        {
            var dto  = new SubmitTaskRequest { ModelProvider = provider };
            var json = JsonSerializer.Serialize(dto, Options);
            var back = JsonSerializer.Deserialize<SubmitTaskRequest>(json, Options)!;
            Assert.Equal(provider, back.ModelProvider);
        }
    }
}
