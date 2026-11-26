using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.MagicCollections.Configuration;

public sealed class PluginConfiguration : BasePluginConfiguration
{
    public string[] SourceUrls { get; set; } = [];
}
