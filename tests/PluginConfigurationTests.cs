using System.Text.Json;
using System.Xml.Serialization;
using Jellyfin.Extensions.Json;
using Jellyfin.Plugin.AutoCollections;
using Jellyfin.Plugin.MagicCollections;
using Jellyfin.Plugin.MagicCollections.Configuration;
using Jellyfin.Plugin.MagicCollections.Sync;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;
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
        const string recommended = "https://app.trakt.tv/discover/recommended?mode=media&ratings=78-100&runtimes=80-500&genres=action";
        const string smartList = "https://app.trakt.tv/lists/smart/view/hidden-gems-6ae39104c63e248a";
        using var update = JsonDocument.Parse($$"""
            {
              "SourceUrls": [
                "{{unrelated}}",
                "http://trakt.tv/users/JUSTIN/lists/OPEN-YOUR-EYES/?access_token=query-token#fragment-token",
                "https://trakt.tv/shows/popular#fragment-token",
                "{{filteredChart}}",
                "http://app.trakt.tv/discover/recommended/?genres=action&runtimes=80-500&mode=media&ratings=78-100&access_token=query-token#fragment-token",
                "{{smartList}}/?access_token=query-token#fragment-token"
              ],
              "TraktClientId": "{{clientId}}",
              "PosterIcon": false
            }
            """);
        var plugin = CreatePlugin();
        IHasPluginConfiguration configuredPlugin = plugin;
        var config = Assert.IsType<PluginConfiguration>(update.Deserialize(configuredPlugin.ConfigurationType, JsonDefaults.Options));
        configuredPlugin.UpdateConfiguration(config);

        var expectedUrls = new[] { unrelated, publicList, "https://app.trakt.tv/discover/popular?mode=show",
            filteredChart, recommended, smartList };
        Assert.Equal(expectedUrls, plugin.Configuration.SourceUrls);
        var xml = File.ReadAllText(plugin.ConfigurationFilePath);
        Assert.DoesNotContain("query-token", xml, StringComparison.Ordinal);
        Assert.DoesNotContain("fragment-token", xml, StringComparison.Ordinal);
        var settings = JsonSerializer.SerializeToElement(CreatePlugin().Configuration, JsonDefaults.PascalCaseOptions);

        Assert.Equal(clientId, settings.GetProperty("TraktClientId").GetString());
        Assert.Equal(expectedUrls, settings.GetProperty("SourceUrls").Deserialize<string[]>());
        Assert.False(settings.GetProperty("PosterIcon").GetBoolean());
    }

    [Fact]
    public async Task Sync_logs_unsupported_URLs_without_userinfo_query_or_fragment()
    {
        var plugin = CreatePlugin();
        plugin.UpdateConfiguration(new PluginConfiguration
        {
            SourceUrls =
            [
                "https://name:private-password@app.trakt.tv/users/justin/lists/open-your-eyes?access_token=private-query#private-fragment",
                "not a URL#private-invalid"
            ]
        });
        var logger = new TaskLog();
        using var http = new HttpClient(new RecordedHttpResponses());
        var task = new SyncTask(new CollectionSync([new TraktHandler(http, () => plugin.Configuration)], new ControlledLibrary()), logger);

        await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        Assert.Equal(2, logger.Warnings.Count);
        Assert.Contains("https://app.trakt.tv/users/justin/lists/open-your-eyes", logger.Warnings[0], StringComparison.Ordinal);
        Assert.All(logger.Warnings, warning => Assert.DoesNotContain("private-", warning, StringComparison.Ordinal));
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

    private sealed class TaskLog : ILogger<SyncTask>
    {
        public List<string> Warnings { get; } = [];
        public bool IsEnabled(LogLevel logLevel) => logLevel == LogLevel.Warning;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                Warnings.Add(formatter(state, exception));
            }
        }
    }
}
