using System.Text.Json;
using System.Xml.Serialization;
using Jellyfin.Extensions.Json;
using Jellyfin.Plugin.MagicCollections;
using Jellyfin.Plugin.MagicCollections.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Serialization;
using Xunit;

namespace MagicCollections.Tests;

public sealed class PluginConfigurationTests : IDisposable
{
    private readonly DirectoryInfo _data = Directory.CreateTempSubdirectory("magic-collections-config-");

    [Theory]
    [InlineData("example-trakt-client-id")]
    [InlineData("")]
    public void Saving_settings_persists_clean_Trakt_URLs_separately_from_the_Client_ID_override(string clientId)
    {
        const string publicList = "https://app.trakt.tv/users/justin/lists/open-your-eyes";
        const string unrelated = "https://letterboxd.com/example/list/movies/?sort=rank#list";
        const string filteredChart = "https://app.trakt.tv/discover/trending?mode=show&genres=comedy";
        using var update = JsonDocument.Parse($$"""
            {
              "SourceUrls": [
                "{{unrelated}}",
                "http://trakt.tv/users/JUSTIN/lists/OPEN-YOUR-EYES/?access_token=query-token#fragment-token",
                "https://trakt.tv/shows/popular#fragment-token",
                "{{filteredChart}}"
              ],
              "TraktClientId": "{{clientId}}",
              "PosterIcon": false
            }
            """);
        var plugin = CreatePlugin();
        IHasPluginConfiguration configuredPlugin = plugin;
        var config = Assert.IsType<PluginConfiguration>(update.Deserialize(configuredPlugin.ConfigurationType, JsonDefaults.Options));
        configuredPlugin.UpdateConfiguration(config);

        var expectedUrls = new[] { unrelated, publicList, "https://app.trakt.tv/discover/popular?mode=show", filteredChart };
        Assert.Equal(expectedUrls, plugin.Configuration.SourceUrls);
        var xml = File.ReadAllText(plugin.ConfigurationFilePath);
        Assert.DoesNotContain("query-token", xml, StringComparison.Ordinal);
        Assert.DoesNotContain("fragment-token", xml, StringComparison.Ordinal);
        var settings = JsonSerializer.SerializeToElement(CreatePlugin().Configuration, JsonDefaults.PascalCaseOptions);

        Assert.Equal(clientId, settings.GetProperty("TraktClientId").GetString());
        Assert.Equal(expectedUrls, settings.GetProperty("SourceUrls").Deserialize<string[]>());
        Assert.False(settings.GetProperty("PosterIcon").GetBoolean());
    }

    public void Dispose() => _data.Delete(recursive: true);

    private Plugin CreatePlugin() => new(new PluginTestServices.TestApplicationPaths(_data.FullName), new ConfigurationXmlSerializer());

    private sealed class ConfigurationXmlSerializer : IXmlSerializer
    {
        public object DeserializeFromStream(Type type, Stream stream) => new XmlSerializer(type).Deserialize(stream)!;
        public void SerializeToStream(object obj, Stream stream) => new XmlSerializer(obj.GetType()).Serialize(stream, obj);

        public void SerializeToFile(object obj, string file)
        {
            using var stream = File.Create(file);
            SerializeToStream(obj, stream);
        }

        public object DeserializeFromFile(Type type, string file)
        {
            using var stream = File.OpenRead(file);
            return DeserializeFromStream(type, stream);
        }

        public object DeserializeFromBytes(Type type, byte[] buffer) => throw new NotSupportedException();
    }
}
