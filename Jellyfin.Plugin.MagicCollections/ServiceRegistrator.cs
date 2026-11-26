using System.Net;
using Jellyfin.Plugin.MagicCollections.Sync;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.MagicCollections;

public sealed class ServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        // Use a dedicated client without replacing Jellyfin's own HTTP services.
        serviceCollection.AddSingleton(_ => new SourceListHttpClient());
        serviceCollection.AddSingleton<ISourceListHandler>(services =>
            new MdblistHandler(services.GetRequiredService<SourceListHttpClient>()));
        serviceCollection.AddSingleton<ISourceListHandler>(services =>
            new PopularMoviesHandler(services.GetRequiredService<SourceListHttpClient>(), TimeProvider.System));
        serviceCollection.AddSingleton<ICollectionLibrary, JellyfinCollectionLibrary>();
        serviceCollection.AddSingleton<CollectionSync>();
    }

    private sealed class SourceListHttpClient() : HttpClient(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        AllowAutoRedirect = false,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.All
    });
}
