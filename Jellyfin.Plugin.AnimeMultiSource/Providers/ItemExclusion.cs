using System;
using Jellyfin.Plugin.AnimeMultiSource.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;

namespace Jellyfin.Plugin.AnimeMultiSource.Providers
{
    internal static class ItemExclusion
    {
        public static bool IsExcluded(BaseItem item, PluginConfiguration config)
        {
            if (IsExcluded(item.Path, config))
            {
                return true;
            }

            // Virtual children may have no path of their own.
            var series = item switch
            {
                Season season => season.Series,
                Episode episode => episode.Series,
                _ => null
            };
            return series != null && IsExcluded(series.Path, config);
        }

        public static bool IsExcluded(string? path, PluginConfiguration config)
        {
            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(config.ExcludedPaths))
            {
                return false;
            }

            var normalizedPath = Normalize(path);
            foreach (var line in config.ExcludedPaths.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var excludedPath = Normalize(line);
                if (excludedPath.Length == 0)
                {
                    continue;
                }

                // Windows drive and UNC paths are case-insensitive, and so are the default macOS
                // volumes; plain Unix paths are case-sensitive.
                var comparison = excludedPath.StartsWith("//", StringComparison.Ordinal)
                    || (excludedPath.Length > 1 && excludedPath[1] == ':')
                    || OperatingSystem.IsMacOS()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal;
                if (normalizedPath.Equals(excludedPath, comparison)
                    || normalizedPath.StartsWith(excludedPath + "/", comparison))
                {
                    return true;
                }
            }

            return false;
        }

        private static string Normalize(string path)
        {
            return path.Trim().Replace('\\', '/').TrimEnd('/');
        }
    }
}
