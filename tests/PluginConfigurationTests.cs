using System.Text.Json;
using System.Xml.Serialization;
using Jellyfin.Extensions.Json;
using Jellyfin.Plugin.MagicCollections.Configuration;
using Xunit;

namespace MagicCollections.Tests;

public sealed class PluginConfigurationTests
{
    [Theory]
    [InlineData("example-trakt-client-id")]
    [InlineData("")]
    public void Trakt_Client_ID_override_round_trips_separately_from_source_list_URLs(string clientId)
    {
        const string sourceUrl = "https://letterboxd.com/example/list/movies/";
        using var update = JsonDocument.Parse($$"""
            { "SourceUrls": ["{{sourceUrl}}"], "TraktClientId": "{{clientId}}", "PosterIcon": false }
            """);
        var config = update.Deserialize<PluginConfiguration>(JsonDefaults.Options);

        // Jellyfin persists plugin configuration with XmlSerializer and serves it as JSON.
        var xml = new XmlSerializer(typeof(PluginConfiguration));
        using var stored = new StringWriter();
        xml.Serialize(stored, config);
        using var persisted = new StringReader(stored.ToString());
        var reloaded = Assert.IsType<PluginConfiguration>(xml.Deserialize(persisted));
        var settings = JsonSerializer.SerializeToElement(reloaded, JsonDefaults.PascalCaseOptions);

        Assert.Equal(clientId, settings.GetProperty("TraktClientId").GetString());
        Assert.Equal(sourceUrl, Assert.Single(settings.GetProperty("SourceUrls").EnumerateArray()).GetString());
        Assert.False(settings.GetProperty("PosterIcon").GetBoolean());
    }
}
