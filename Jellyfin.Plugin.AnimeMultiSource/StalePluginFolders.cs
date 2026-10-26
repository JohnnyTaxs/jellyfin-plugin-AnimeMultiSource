using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.AnimeMultiSource
{
    /// <summary>
    /// Finds the folders of OLDER versions of this plugin that an update left behind. Jellyfin installs an update into a
    /// folder named after the manifest ("Anime Multi Source_1.1.0.0", with spaces - not the plugin's own name) and keeps the
    /// old version's folder active until a later restart, so on that restart both versions are loaded at once.
    /// </summary>
    public static class StalePluginFolders
    {
        private static readonly Regex NameLike = new(@"^anime[ _-]?multi[ _-]?source(_|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static IReadOnlyList<string> Find(string pluginsPath, string ownDirectory, string ownDllName, Guid ownGuid, Version ownVersion)
        {
            var stale = new List<string>();
            if (!Directory.Exists(pluginsPath))
            {
                return stale;
            }

            foreach (var directory in Directory.GetDirectories(pluginsPath))
            {
                if (string.Equals(Path.GetFullPath(directory), Path.GetFullPath(ownDirectory), StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // Only a folder that really holds this plugin's assembly can be a version of it.
                if (!File.Exists(Path.Combine(directory, ownDllName)))
                {
                    continue;
                }

                var (guid, version) = ReadMeta(directory);
                var folderName = Path.GetFileName(directory);
                var isOurs = guid.HasValue ? guid.Value == ownGuid : NameLike.IsMatch(folderName);
                if (!isOurs)
                {
                    continue;
                }

                version ??= VersionFromFolderName(folderName);

                // Never touch an equal or newer version (a downgrade must not delete the newer install), nor one whose
                // version cannot be read.
                if (version == null || version >= ownVersion)
                {
                    continue;
                }

                stale.Add(directory);
            }

            return stale;
        }

        /// <summary>Marks a folder that could not be deleted (a locked file on Windows) as disabled, so Jellyfin skips it.</summary>
        public static bool TryDisable(string directory)
        {
            try
            {
                var metaPath = Path.Combine(directory, "meta.json");
                if (!File.Exists(metaPath))
                {
                    return false;
                }

                var text = File.ReadAllText(metaPath);
                var updated = Regex.Replace(text, "\"status\"\\s*:\\s*\"[^\"]*\"", "\"status\": \"Disabled\"", RegexOptions.IgnoreCase);
                if (updated == text)
                {
                    return false;
                }

                File.WriteAllText(metaPath, updated);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        private static (Guid? Guid, Version? Version) ReadMeta(string directory)
        {
            try
            {
                var metaPath = Path.Combine(directory, "meta.json");
                if (!File.Exists(metaPath))
                {
                    return (null, null);
                }

                using var document = JsonDocument.Parse(File.ReadAllText(metaPath));
                var root = document.RootElement;
                Guid? guid = root.TryGetProperty("guid", out var g) && g.ValueKind == JsonValueKind.String && Guid.TryParse(g.GetString(), out var parsedGuid)
                    ? parsedGuid
                    : null;
                Version? version = root.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String && Version.TryParse(v.GetString(), out var parsedVersion)
                    ? parsedVersion
                    : null;
                return (guid, version);
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                return (null, null);
            }
        }

        private static Version? VersionFromFolderName(string folderName)
        {
            var separator = folderName.LastIndexOf('_');
            return separator >= 0 && Version.TryParse(folderName[(separator + 1)..], out var version) ? version : null;
        }
    }
}
