using Jellyfin.Plugin.MagicCollections;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MagicCollections.Tests;

internal static class PluginTestServices
{
    public static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        var dataPath = Path.Combine(Path.GetTempPath(), $"magic-collections-tests-{Guid.NewGuid():N}");
        services.AddSingleton<IApplicationPaths>(new TestApplicationPaths(dataPath));
        new ServiceRegistrator().RegisterServices(services, null!);
        return services.BuildServiceProvider();
    }

    private sealed class TestApplicationPaths(string dataPath) : IApplicationPaths
    {
        public string DataPath => dataPath;
        public string ProgramDataPath => throw new NotSupportedException();
        public string WebPath => throw new NotSupportedException();
        public string ProgramSystemPath => throw new NotSupportedException();
        public string ImageCachePath => throw new NotSupportedException();
        public string PluginsPath => throw new NotSupportedException();
        public string PluginConfigurationsPath => throw new NotSupportedException();
        public string LogDirectoryPath => throw new NotSupportedException();
        public string ConfigurationDirectoryPath => throw new NotSupportedException();
        public string SystemConfigurationFilePath => throw new NotSupportedException();
        public string CachePath => throw new NotSupportedException();
        public string TempDirectory => throw new NotSupportedException();
        public string VirtualDataPath => throw new NotSupportedException();
        public string TrickplayPath => throw new NotSupportedException();
        public string BackupPath => throw new NotSupportedException();
        public void MakeSanityCheckOrThrow() => throw new NotSupportedException();
        public void CreateAndCheckMarker(string path, string markerName, bool recursive) => throw new NotSupportedException();
    }
}
