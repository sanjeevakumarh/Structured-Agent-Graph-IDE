namespace SAGIDE.Core.DTOs;

/// <summary>
/// Per-step model override supplied at workflow launch time.
/// Resolution chain: YAML step model → StepModelOverride → instance default → TaskAffinities
/// </summary>
public class StepModelOverride
{
    public string Provider { get; set; } = string.Empty;
    public string ModelId  { get; set; } = string.Empty;
    /// <summary>Ollama server URL; omit for cloud models.</summary>
    public string? Endpoint { get; set; }
}

public class StartWorkflowRequest
{
    public string DefinitionId { get; set; } = string.Empty;

    /// <summary>Values for YAML parameters (e.g. { "feature_description": "Add login" }).</summary>
    public Dictionary<string, string> Inputs { get; set; } = [];

    public List<string> FilePaths { get; set; } = [];
    public string DefaultModelId { get; set; } = string.Empty;
    public string DefaultModelProvider { get; set; } = string.Empty;

    /// <summary>Explicit Ollama server URL override (same as task-level modelEndpoint).</summary>
    public string? ModelEndpoint { get; set; }

    /// <summary>Workspace directory — used to discover custom .sagide/workflows/*.yaml files.</summary>
    public string? WorkspacePath { get; set; }

    /// <summary>
    /// Per-step model overrides selected at launch time (keyed by step ID).
    /// Only meaningful for agent steps that don't have a model baked into the YAML.
    /// </summary>
    public Dictionary<string, StepModelOverride> StepModelOverrides { get; set; } = [];
}

public class GetWorkflowsRequest
{
    public string? WorkspacePath { get; set; }
}

public class CancelWorkflowRequest
{
    public string InstanceId { get; set; } = string.Empty;
}

public class WorkflowStartedResponse
{
    public string InstanceId { get; set; } = string.Empty;
}

/// <summary>Client → server: human approves or rejects a human_approval gate step.</summary>
public class ApproveWorkflowStepRequest
{
    public string InstanceId { get; set; } = string.Empty;
    public string StepId { get; set; } = string.Empty;
    public bool Approved { get; set; }
    /// <summary>Optional comment recorded in the step output.</summary>
    public string? Comment { get; set; }
}
