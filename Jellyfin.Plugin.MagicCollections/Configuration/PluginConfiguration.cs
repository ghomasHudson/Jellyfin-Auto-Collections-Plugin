using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.MagicCollections.Configuration;

public sealed class PluginConfiguration : BasePluginConfiguration
{
    public string[] SourceUrls { get; set; } = [];
    public string TraktClientId { get; set; } = string.Empty;
    public string[] PrivateTraktUrls { get; set; } = [];
    public bool PosterBackgroundImage { get; set; } = true;
    public bool PosterIcon { get; set; } = true;
    public bool PosterListName { get; set; } = true;
    public bool VerboseLogging { get; set; }
}
