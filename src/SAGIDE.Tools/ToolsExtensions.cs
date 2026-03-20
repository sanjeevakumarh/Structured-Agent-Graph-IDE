using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SAGIDE.Core.Interfaces;

namespace SAGIDE.Tools;

/// <summary>
/// Core DI registration for the SAGIDE tools module.
///
/// Registers the <see cref="IToolRegistry"/> singleton as an empty
/// <see cref="InProcessToolRegistry"/>. Concrete tool registrations
/// (WebFetchTool, WebSearchTool, GitTool) are wired from the composition
/// root (<c>SAGIDE.Service.Infrastructure.ServiceCollectionExtensions</c>)
/// which has direct references to WebFetcher, WebSearchAdapter, and GitService.
/// </summary>
public static class ToolsExtensions
{
    public static IServiceCollection AddSagideTools(this IServiceCollection services)
    {
        services.AddSingleton<IToolRegistry>(sp => new InProcessToolRegistry(
            sp.GetRequiredService<ILogger<InProcessToolRegistry>>(),
            sp.GetService<IAuditLog>()));

        return services;
    }
}
