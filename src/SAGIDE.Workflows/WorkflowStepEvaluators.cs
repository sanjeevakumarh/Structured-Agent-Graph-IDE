using System.Text.RegularExpressions;
using SAGIDE.Core.Models;

namespace SAGIDE.Workflows;

/// <summary>
/// Pure static evaluation helpers extracted from WorkflowEngine.
/// No dependencies, no I/O — all methods operate on WorkflowInstance/WorkflowDefinition value graphs.
/// </summary>
public static class WorkflowStepEvaluators
{
    // ── Router evaluation ──────────────────────────────────────────────────────

    /// <summary>
    /// Evaluates router branches against the dependency executions and returns
    /// the target step ID of the first matching branch, or null if none match.
    /// </summary>
    public static string? EvaluateRouter(WorkflowStepDef routerStep, WorkflowInstance inst)
    {
        if (routerStep.Router is null) return null;

        var depExecs = routerStep.DependsOn
            .Select(id => inst.StepExecutions.GetValueOrDefault(id))
            .Where(e => e is not null)
            .Cast<WorkflowStepExecution>()
            .ToList();

        var primaryDep = depExecs.OrderByDescending(e => e.IssueCount).FirstOrDefault()
                      ?? depExecs.FirstOrDefault();

        if (primaryDep is null) return null;

        foreach (var branch in routerStep.Router.Branches)
        {
            if (EvaluateCondition(branch.Condition, primaryDep))
                return branch.Target;
        }
        return null;
    }

    public static bool EvaluateCondition(string condition, WorkflowStepExecution dep)
    {
        var c = condition.Trim().ToLowerInvariant();
        return c switch
        {
            "hasissues" or "has_issues"    => dep.IssueCount > 0,
            "success"   or "approved"      => dep.Status == WorkflowStepStatus.Completed && dep.IssueCount == 0,
            "failed"    or "error"         => dep.Status == WorkflowStepStatus.Failed,
            _ when c.StartsWith("output.contains(") => EvaluateContains(c, dep.Output ?? ""),
            _ => false,
        };
    }

    public static bool EvaluateContains(string condition, string output)
    {
        var start = condition.IndexOf('(') + 1;
        var end   = condition.LastIndexOf(')');
        if (start >= end) return false;
        var arg = condition[start..end].Trim('\'', '"', ' ');
        return output.Contains(arg, StringComparison.OrdinalIgnoreCase);
    }

    // ── Constraint expression evaluation ──────────────────────────────────────

    public static (bool Passed, string Reason) EvaluateConstraintExpr(string expr, WorkflowInstance inst)
    {
        expr = expr.Trim();

        // exit_code(step_id) OP N
        var m = Regex.Match(expr, @"exit_code\((\w+)\)\s*(==|!=|>=|<=|>|<)\s*(-?\d+)", RegexOptions.IgnoreCase);
        if (m.Success)
        {
            var stepId   = m.Groups[1].Value;
            var op       = m.Groups[2].Value;
            var expected = int.Parse(m.Groups[3].Value);
            if (inst.StepExecutions.TryGetValue(stepId, out var e) && e.ExitCode.HasValue)
                return (CompareInts(e.ExitCode.Value, op, expected),
                    $"exit_code({stepId}) {op} {expected} — actual={e.ExitCode.Value}");
            return (false, $"Step '{stepId}' has no exit code recorded.");
        }

        // output(step_id).contains('text')
        m = Regex.Match(expr, @"output\((\w+)\)\.contains\('(.+?)'\)", RegexOptions.IgnoreCase);
        if (m.Success)
        {
            var stepId = m.Groups[1].Value;
            var text   = m.Groups[2].Value;
            if (inst.StepExecutions.TryGetValue(stepId, out var e))
            {
                var contains = (e.Output ?? "").Contains(text, StringComparison.OrdinalIgnoreCase);
                return (contains, $"output({stepId}).contains('{text}') = {contains}");
            }
            return (false, $"Step '{stepId}' not found.");
        }

        // issue_count(step_id) OP N
        m = Regex.Match(expr, @"issue_count\((\w+)\)\s*(==|!=|>=|<=|>|<)\s*(\d+)", RegexOptions.IgnoreCase);
        if (m.Success)
        {
            var stepId   = m.Groups[1].Value;
            var op       = m.Groups[2].Value;
            var expected = int.Parse(m.Groups[3].Value);
            if (inst.StepExecutions.TryGetValue(stepId, out var e))
                return (CompareInts(e.IssueCount, op, expected),
                    $"issue_count({stepId}) {op} {expected} — actual={e.IssueCount}");
            return (false, $"Step '{stepId}' not found.");
        }

        // output_value(step_id) OP N — first numeric value in output
        m = Regex.Match(expr, @"output_value\((\w+)\)\s*(==|!=|>=|<=|>|<)\s*(-?\d+(?:\.\d+)?)", RegexOptions.IgnoreCase);
        if (m.Success)
        {
            var stepId   = m.Groups[1].Value;
            var op       = m.Groups[2].Value;
            var expected = double.Parse(m.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture);
            if (inst.StepExecutions.TryGetValue(stepId, out var e))
            {
                var numMatch = Regex.Match(e.Output ?? "", @"-?\d+(?:\.\d+)?");
                if (numMatch.Success &&
                    double.TryParse(numMatch.Value, System.Globalization.NumberStyles.Number,
                        System.Globalization.CultureInfo.InvariantCulture, out var actual))
                    return (CompareDoubles(actual, op, expected),
                        $"output_value({stepId}) {op} {expected} — actual={actual}");
                return (false, $"Step '{stepId}' output contains no numeric value.");
            }
            return (false, $"Step '{stepId}' not found.");
        }

        // output_value(step_id, 'metric_name') OP N — named metric
        m = Regex.Match(expr,
            @"output_value\((\w+),\s*'([^']+)'\)\s*(==|!=|>=|<=|>|<)\s*(-?\d+(?:\.\d+)?)",
            RegexOptions.IgnoreCase);
        if (m.Success)
        {
            var stepId     = m.Groups[1].Value;
            var metricName = m.Groups[2].Value;
            var op         = m.Groups[3].Value;
            var expected   = double.Parse(m.Groups[4].Value, System.Globalization.CultureInfo.InvariantCulture);
            if (inst.StepExecutions.TryGetValue(stepId, out var e))
            {
                var pattern  = Regex.Escape(metricName) + @"[:\s=]+(-?\d+(?:\.\d+)?)";
                var numMatch = Regex.Match(e.Output ?? "", pattern, RegexOptions.IgnoreCase);
                if (numMatch.Success &&
                    double.TryParse(numMatch.Groups[1].Value, System.Globalization.NumberStyles.Number,
                        System.Globalization.CultureInfo.InvariantCulture, out var actual))
                    return (CompareDoubles(actual, op, expected),
                        $"output_value({stepId}, '{metricName}') {op} {expected} — actual={actual}");
                return (false, $"Step '{stepId}' output does not contain metric '{metricName}'.");
            }
            return (false, $"Step '{stepId}' not found.");
        }

        // delta_issues(current, baseline) OP N
        m = Regex.Match(expr,
            @"delta_issues\((\w+),\s*(\w+)\)\s*(==|!=|>=|<=|>|<)\s*(-?\d+)",
            RegexOptions.IgnoreCase);
        if (m.Success)
        {
            var currentId  = m.Groups[1].Value;
            var baselineId = m.Groups[2].Value;
            var op         = m.Groups[3].Value;
            var expected   = int.Parse(m.Groups[4].Value);
            var hasCurrentStep  = inst.StepExecutions.TryGetValue(currentId,  out var current);
            var hasBaselineStep = inst.StepExecutions.TryGetValue(baselineId, out var baseline);
            if (!hasCurrentStep)  return (false, $"Step '{currentId}' not found.");
            if (!hasBaselineStep) return (false, $"Step '{baselineId}' not found.");
            var delta = current!.IssueCount - baseline!.IssueCount;
            return (CompareInts(delta, op, expected),
                $"delta_issues({currentId}, {baselineId}) {op} {expected} — delta={delta} ({current.IssueCount}-{baseline.IssueCount})");
        }

        // confidence(step_id) OP N
        m = Regex.Match(expr,
            @"confidence\((\w+)\)\s*(==|!=|>=|<=|>|<)\s*(-?\d+(?:\.\d+)?)",
            RegexOptions.IgnoreCase);
        if (m.Success)
        {
            var stepId   = m.Groups[1].Value;
            var op       = m.Groups[2].Value;
            var expected = double.Parse(m.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture);
            if (inst.StepExecutions.TryGetValue(stepId, out var e))
            {
                var conf = e.IntentPackage?.Confidence;
                if (conf.HasValue)
                    return (CompareDoubles(conf.Value, op, expected),
                        $"confidence({stepId}) {op} {expected} — actual={conf.Value:F3}");
                return (false, $"Step '{stepId}' has no IntentPackage.Confidence (agent step required).");
            }
            return (false, $"Step '{stepId}' not found.");
        }

        return (false, $"Unrecognized constraint expression: '{expr}'");
    }

    public static bool CompareInts(int actual, string op, int expected) => op switch
    {
        "==" => actual == expected,
        "!=" => actual != expected,
        ">=" => actual >= expected,
        "<=" => actual <= expected,
        ">"  => actual >  expected,
        "<"  => actual <  expected,
        _    => false,
    };

    public static bool CompareDoubles(double actual, string op, double expected) => op switch
    {
        "==" => Math.Abs(actual - expected) < 1e-9,
        "!=" => Math.Abs(actual - expected) >= 1e-9,
        ">=" => actual >= expected,
        "<=" => actual <= expected,
        ">"  => actual >  expected,
        "<"  => actual <  expected,
        _    => false,
    };

    // ── DAG helpers ────────────────────────────────────────────────────────────

    public static bool IsInstanceDone(WorkflowInstance inst, WorkflowDefinition def)
        => def.Steps
              .Where(s => s.Type is not "router")
              .All(s => inst.StepExecutions.TryGetValue(s.Id, out var e)
                        && e.Status is WorkflowStepStatus.Completed
                                    or WorkflowStepStatus.Failed
                                    or WorkflowStepStatus.Skipped
                                    or WorkflowStepStatus.Rejected);

    public static void SkipDownstream(string failedStepId, WorkflowInstance inst, WorkflowDefinition def)
    {
        var queue   = new Queue<string>([failedStepId]);
        var visited = new HashSet<string>();
        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            if (!visited.Add(id)) continue;
            foreach (var step in def.Steps.Where(s => s.DependsOn.Contains(id)))
            {
                if (inst.StepExecutions.TryGetValue(step.Id, out var e)
                    && e.Status == WorkflowStepStatus.Pending)
                {
                    RecordAudit(e, WorkflowStepStatus.Skipped, $"Upstream step '{failedStepId}' failed/skipped");
                    queue.Enqueue(step.Id);
                }
            }
        }
    }

    // ── Audit helper ───────────────────────────────────────────────────────────

    public static void RecordAudit(
        WorkflowStepExecution exec, WorkflowStepStatus newStatus, string? reason = null)
    {
        exec.AuditLog.Add(new AuditEntry
        {
            FromStatus = exec.Status,
            ToStatus   = newStatus,
            Reason     = reason,
        });
        exec.Status = newStatus;
    }

    // ── Convergence loop helpers ───────────────────────────────────────────────

    public static void ResetAllStepsForNewIteration(
        string loopTargetId, WorkflowInstance inst, WorkflowDefinition def)
    {
        foreach (var s in def.Steps.Where(s => s.Id != loopTargetId))
        {
            var se = inst.StepExecutions[s.Id];
            if (se.Status is WorkflowStepStatus.WaitingForApproval or WorkflowStepStatus.Rejected)
                continue;
            se.Status     = WorkflowStepStatus.Pending;
            se.Output     = null;
            se.TaskId     = null;
            se.Error      = null;
            se.ExitCode   = null;
            se.IssueCount = 0;
        }
    }

    public static void ResetLoopBodyForNewIteration(
        string loopTargetId, WorkflowInstance inst, WorkflowDefinition def)
    {
        foreach (var stepId in GetDescendantStepIds(loopTargetId, def))
        {
            var se = inst.StepExecutions[stepId];
            if (se.Status is WorkflowStepStatus.WaitingForApproval or WorkflowStepStatus.Rejected)
                continue;
            se.Status     = WorkflowStepStatus.Pending;
            se.Output     = null;
            se.TaskId     = null;
            se.Error      = null;
            se.ExitCode   = null;
            se.IssueCount = 0;
        }
    }

    /// <summary>BFS forward from rootId; returns all reachable step IDs (excluding rootId itself).</summary>
    public static HashSet<string> GetDescendantStepIds(string rootId, WorkflowDefinition def)
    {
        var result = new HashSet<string>();
        var queue  = new Queue<string>();
        queue.Enqueue(rootId);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var s in def.Steps.Where(s => s.DependsOn.Contains(current)))
            {
                if (result.Add(s.Id))
                    queue.Enqueue(s.Id);
            }
        }
        return result;
    }

    public static void InjectConvergenceHints(
        WorkflowStepDef validationStep,
        WorkflowStepExecution validationExec,
        WorkflowStepExecution refactorExec,
        WorkflowInstance inst)
    {
        var failureLines = new System.Text.StringBuilder();
        failureLines.AppendLine($"[Iteration {refactorExec.Iteration} causal memory]");
        failureLines.AppendLine($"Step '{validationStep.Id}' reported {validationExec.IssueCount} issue(s).");

        if (!string.IsNullOrWhiteSpace(validationExec.Output))
        {
            var trimmed = validationExec.Output.Length > 800
                ? validationExec.Output[..800] + "…"
                : validationExec.Output;
            failureLines.AppendLine($"Constraint output: {trimmed}");
        }

        if (!string.IsNullOrWhiteSpace(validationExec.Error))
            failureLines.AppendLine($"Error: {validationExec.Error}");

        inst.InputContext["convergence_hints"] = failureLines.ToString().Trim();
    }
}
