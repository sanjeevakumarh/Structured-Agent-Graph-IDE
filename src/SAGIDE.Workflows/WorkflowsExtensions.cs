using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SAGIDE.Core.Events;
using SAGIDE.Core.Interfaces;
using SAGIDE.Core.Models;

namespace SAGIDE.Workflows;

/// <summary>
/// DI registration for the SAGIDE.Workflows module.
///
/// Usage in the composition root:
/// <code>
///   services.AddSagideWorkflows(configuration);
/// </code>
///
/// Prerequisites — must be registered before calling this:
///   - <c>ITaskSubmissionService</c> (from orchestration)
///   - <c>IWorkflowRepository</c> (from persistence)
///   - <c>IWorkflowGitService</c> (alias for GitService)
///   - <c>IWorkflowStepRenderer</c> (adapter for PromptTemplate)
///   - <c>IEventBus</c> (from events)
///   - Config singletons: <c>AgentLimitsConfig</c>, <c>TaskAffinitiesConfig</c>, <c>WorkflowPolicyConfig</c>
/// </summary>
public static class WorkflowsExtensions
{
    public static IServiceCollection AddSagideWorkflows(
        this IServiceCollection services,
        Microsoft.Extensions.Configuration.IConfiguration configuration)
    {
        // WorkflowPolicyConfig — bind from config or use defaults
        var policyConfig = new WorkflowPolicyConfig();
        configuration.GetSection("SAGIDE:WorkflowPolicy").Bind(policyConfig);
        services.AddSingleton(policyConfig);

        // WorkflowPolicyEngine
        services.AddSingleton<WorkflowPolicyEngine>();

        // WorkflowDefinitionLoader
        services.AddSingleton<WorkflowDefinitionLoader>();

        // WorkflowEngine — concrete + IWorkflowEngine alias
        services.AddSingleton<WorkflowEngine>(sp => new WorkflowEngine(
            sp.GetRequiredService<ITaskSubmissionService>(),
            sp.GetRequiredService<WorkflowDefinitionLoader>(),
            sp.GetRequiredService<AgentLimitsConfig>(),
            sp.GetRequiredService<TaskAffinitiesConfig>(),
            sp.GetRequiredService<WorkflowPolicyEngine>(),
            sp.GetRequiredService<IWorkflowGitService>(),
            sp.GetRequiredService<IWorkflowStepRenderer>(),
            sp.GetRequiredService<ILogger<WorkflowEngine>>(),
            sp.GetService<IWorkflowRepository>(),
            sp.GetService<IEventBus>(),
            sp.GetService<ILoggerFactory>()));

        services.AddSingleton<IWorkflowEngine>(sp =>
            sp.GetRequiredService<WorkflowEngine>());

        return services;
    }


}
