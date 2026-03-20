using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SAGIDE.Core.Interfaces;

namespace SAGIDE.Memory;

/// <summary>
/// DI registration for the SAGIDE.Memory module.
///
/// Registers: WebFetcher, WebSearchAdapter, EmbeddingService, TextChunker,
/// VectorStore, RagPipeline, NotesIndexerService (conditional).
///
/// Usage in the composition root:
/// <code>
///   services.AddSagideMemory(configuration, dbPath);
/// </code>
///
/// Prerequisites — must be registered before calling this:
///   - <c>INotesFileIndexRepository</c> (from persistence)
///   - <c>ISearchCacheRepository</c> (from persistence)
/// </summary>
public static class MemoryExtensions
{
    public static IServiceCollection AddSagideMemory(
        this IServiceCollection services,
        IConfiguration configuration,
        string dbPath)
    {
        // WebFetcher — typed HttpClient with redirect + decompression support
        services.AddHttpClient<WebFetcher>(client =>
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd("SAGIDE/1.0");
        }).ConfigurePrimaryHttpMessageHandler(() => new System.Net.Http.HttpClientHandler
        {
            AllowAutoRedirect        = true,
            MaxAutomaticRedirections = 10,
            AutomaticDecompression   = System.Net.DecompressionMethods.All,
        });

        services.AddHttpClient<WebSearchAdapter>();
        services.AddHttpClient<EmbeddingService>();
        services.AddSingleton<TextChunker>();

        // VectorStore — SQLite-backed embedding store
        services.AddSingleton(sp =>
            new VectorStore(dbPath, sp.GetRequiredService<ILogger<VectorStore>>()));

        // RagPipeline + IMemorySystem alias
        services.AddSingleton<RagPipeline>();
        services.AddSingleton<IMemorySystem>(sp => sp.GetRequiredService<RagPipeline>());

        // Notes indexer — optional background service for Logseq graph embedding
        var notesConfig = new NotesConfig();
        configuration.GetSection("SAGIDE:Notes").Bind(notesConfig);
        services.AddSingleton(notesConfig);
        if (notesConfig.Enabled)
        {
            services.AddSingleton<NotesIndexerService>();
            services.AddSingleton<IHostedService>(sp =>
                sp.GetRequiredService<NotesIndexerService>());
        }

        return services;
    }
}
