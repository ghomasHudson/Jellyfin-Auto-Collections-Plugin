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
        // Keep one client for the lifetime of the plugin without replacing Jellyfin's own HTTP services.
        serviceCollection.AddSingleton(_ => new PublicListHttpClient());
        serviceCollection.AddSingleton<ISourceListHandler>(services =>
            new MdblistHandler(services.GetRequiredService<PublicListHttpClient>()));
        serviceCollection.AddSingleton<ISourceListHandler>(services =>
            new LetterboxdHandler(services.GetRequiredService<PublicListHttpClient>()));
        serviceCollection.AddSingleton<ICollectionLibrary, JellyfinCollectionLibrary>();
        serviceCollection.AddSingleton<CollectionSync>();
    }

    private sealed class PublicListHttpClient() : HttpClient(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        AllowAutoRedirect = false,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.All
    });
}
