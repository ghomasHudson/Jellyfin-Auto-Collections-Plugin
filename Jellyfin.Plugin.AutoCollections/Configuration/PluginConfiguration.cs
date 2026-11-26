using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.AutoCollections.Configuration;

public sealed class PluginConfiguration : BasePluginConfiguration
{
    public string[] SourceUrls { get; set; } = [];
}
