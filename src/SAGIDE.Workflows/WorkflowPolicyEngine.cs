using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using SAGIDE.Core.Models;

namespace SAGIDE.Workflows;

/// <summary>
/// Evaluates per-step policy rules before a workflow step is submitted to the orchestrator.
/// Prevents workflows from automating forbidden actions such as operating on protected files
/// or using blocked agent types.
///
/// Configuration is read from appsettings.json under SAGIDE:WorkflowPolicy.
/// </summary>
public class WorkflowPolicyEngine
{
    private readonly WorkflowPolicyConfig _config;
    private readonly ILogger<WorkflowPolicyEngine> _logger;

    // Pre-compiled glob patterns (converted to Regex once at startup)
    private readonly List<Regex> _protectedPatterns;

    public WorkflowPolicyEngine(WorkflowPolicyConfig config, ILogger<WorkflowPolicyEngine> logger)
    {
        _config = config;
        _logger = logger;
        _protectedPatterns = config.ProtectedPathPatterns
            .Select(p => GlobToRegex(p))
            .ToList();
    }

    /// <summary>
    /// Checks whether a workflow step is allowed to run.
    /// Returns a <see cref="PolicyCheckResult"/> describing the outcome.
    /// </summary>
    public PolicyCheckResult Check(WorkflowStepDef stepDef, WorkflowInstance inst)
    {
        if (!_config.Enabled)
            return PolicyCheckResult.Allow();

        // 1. Blocked agent types
        var agentName = stepDef.Agent ?? stepDef.Id;
        if (_config.BlockedAgentTypes.Any(b =>
                string.Equals(b, agentName, StringComparison.OrdinalIgnoreCase)))
        {
            var reason = $"Agent '{agentName}' is blocked by workflow policy (BlockedAgentTypes).";
            _logger.LogWarning("Policy DENY — {Reason} (instance {Id}, step {Step})",
                reason, inst.InstanceId, stepDef.Id);
            return PolicyCheckResult.Deny(reason);
        }

        // 2. Protected file paths
        foreach (var filePath in inst.FilePaths)
        {
            var normalized = filePath.Replace('\\', '/');
            foreach (var pattern in _protectedPatterns)
            {
                if (pattern.IsMatch(normalized))
                {
                    var reason = $"File '{filePath}' matches protected path pattern " +
                                 $"'{_config.ProtectedPathPatterns[_protectedPatterns.IndexOf(pattern)]}'.";
                    _logger.LogWarning("Policy DENY — {Reason} (instance {Id}, step {Step})",
                        reason, inst.InstanceId, stepDef.Id);
                    return PolicyCheckResult.Deny(reason);
                }
            }
        }

        // 3. Max steps guard
        if (_config.MaxStepsPerWorkflow > 0)
        {
            var nonRouterSteps = inst.StepExecutions.Values
                .Count(s => s.Status != WorkflowStepStatus.Pending);
            if (nonRouterSteps >= _config.MaxStepsPerWorkflow)
            {
                var reason = $"Workflow has executed {nonRouterSteps} steps, " +
                             $"which exceeds MaxStepsPerWorkflow={_config.MaxStepsPerWorkflow}.";
                _logger.LogWarning("Policy DENY — {Reason} (instance {Id})", reason, inst.InstanceId);
                return PolicyCheckResult.Deny(reason);
            }
        }

        return PolicyCheckResult.Allow();
    }

    // ── Glob → Regex conversion ───────────────────────────────────────────────

    /// <summary>Converts a simple glob pattern (*, **, ?) to a compiled Regex.</summary>
    private static Regex GlobToRegex(string glob)
    {
        // Normalize path separators
        glob = glob.Replace('\\', '/');

        var sb = new System.Text.StringBuilder("^");
        var i = 0;
        while (i < glob.Length)
        {
            if (glob[i] == '*' && i + 1 < glob.Length && glob[i + 1] == '*')
            {
                // ** matches any path segment including /
                sb.Append(".*");
                i += 2;
                if (i < glob.Length && glob[i] == '/') i++; // skip trailing /
            }
            else if (glob[i] == '*')
            {
                // * matches anything except /
                sb.Append("[^/]*");
                i++;
            }
            else if (glob[i] == '?')
            {
                sb.Append("[^/]");
                i++;
            }
            else
            {
                sb.Append(Regex.Escape(glob[i].ToString()));
                i++;
            }
        }
        sb.Append('$');
        return new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.Compiled);
    }
}

public class PolicyCheckResult
{
    public bool IsAllowed { get; }
    public string? DenyReason { get; }

    private PolicyCheckResult(bool allowed, string? reason)
    {
        IsAllowed  = allowed;
        DenyReason = reason;
    }

    public static PolicyCheckResult Allow() => new(true, null);
    public static PolicyCheckResult Deny(string reason) => new(false, reason);
}
