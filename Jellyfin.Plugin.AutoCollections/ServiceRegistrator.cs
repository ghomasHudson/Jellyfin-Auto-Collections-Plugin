using Jellyfin.Plugin.AutoCollections.Sync;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.AutoCollections;

public sealed class ServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<ICollectionLibrary, JellyfinCollectionLibrary>();
        serviceCollection.AddSingleton<CollectionSync>();
    }
}
