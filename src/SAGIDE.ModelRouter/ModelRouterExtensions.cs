using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SAGIDE.Core.Interfaces;
using SAGIDE.Core.Models;

namespace SAGIDE.ModelRouter;

/// <summary>
/// DI registration for the ModelRouter module.
///
/// Usage in <c>Program.cs</c> (or any composition root):
/// <code>
///   builder.Services.AddSagideModelRouter();
/// </code>
///
/// Prerequisites — must be registered before calling this:
///   - All <see cref="IAgentProvider"/> singletons (done by <c>AddSagideProviders</c>)
///   - Optionally a <c>CircuitBreakerRegistry</c> for circuit-breaker integration
/// </summary>
public static class ModelRouterExtensions
{
    public static IServiceCollection AddSagideModelRouter(this IServiceCollection services)
    {
        services.AddSingleton<IModelRouter>(sp =>
        {
            var providers = sp.GetServices<IAgentProvider>();
            var logger    = sp.GetRequiredService<ILogger<ModelRouter>>();

            // Wire circuit-breaker integration if the registry is registered.
            // Avoids a hard dependency on SAGIDE.Service.Resilience from this assembly.
            Func<ModelProvider, bool>? isCircuitOpen = null;
            var cbRegistry = sp.GetService<SAGIDE.Core.Interfaces.ICircuitBreakerRegistry>();
            if (cbRegistry is not null)
                isCircuitOpen = provider => !cbRegistry.IsCallPermitted(provider);

            return new ModelRouter(providers, isCircuitOpen, logger);
        });

        return services;
    }
}
