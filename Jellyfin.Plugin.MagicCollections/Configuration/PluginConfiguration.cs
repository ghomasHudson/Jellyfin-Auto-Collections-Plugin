using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.MagicCollections.Configuration;

public sealed class PluginConfiguration : BasePluginConfiguration
{
    public string[] SourceUrls { get; set; } = [];
    public bool PosterBackgroundImage { get; set; } = true;
    public bool PosterIcon { get; set; } = true;
    public bool PosterListName { get; set; } = true;
}
