using System;
using System.IO;
using System.Text;
using System.Xml;
using System.Xml.Serialization;

namespace Jellyfin.Plugin.AnimeMultiSource.Configuration
{
    /// <summary>
    /// Reads and writes the plugin's settings file with our own serializer. Jellyfin's loader keeps its serializers in a cache
    /// keyed by the class NAME, so when an update leaves two versions of this plugin loaded side by side, the second one to
    /// read the file is handed the first one's class, the cast fails, and Jellyfin's fallback saves a default configuration
    /// over the user's settings. A serializer created here is tied to our own class, so that cannot happen to this copy.
    /// </summary>
    public static class PluginSettingsStore
    {
        private static readonly XmlSerializer Serializer = new(typeof(PluginConfiguration));

        public static string BackupPathFor(string settingsPath) => settingsPath + ".backup";

        /// <summary>The settings in the file, or null when the file is missing, empty or unreadable.</summary>
        public static PluginConfiguration? Read(string path)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return null;
                }

                using var stream = File.OpenRead(path);
                return Serializer.Deserialize(stream) as PluginConfiguration;
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or XmlException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        /// <summary>Writes the settings through a temporary file, so a crash never leaves a half-written file behind.</summary>
        public static void Write(PluginConfiguration config, string path)
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temp = path + ".tmp";
            using (var writer = XmlWriter.Create(temp, new XmlWriterSettings { Indent = true, Encoding = new UTF8Encoding(false) }))
            {
                Serializer.Serialize(writer, config);
            }

            File.Move(temp, path, overwrite: true);
        }

        public static string Serialize(PluginConfiguration config)
        {
            using var writer = new StringWriter();
            Serializer.Serialize(writer, config);
            return writer.ToString();
        }

        /// <summary>True when no setting differs from a fresh install - what a "reset by another copy" file looks like.</summary>
        public static bool IsDefault(PluginConfiguration config) =>
            string.Equals(Serialize(config), Serialize(new PluginConfiguration()), StringComparison.Ordinal);

        public static bool AreEqual(PluginConfiguration? a, PluginConfiguration? b) =>
            a != null && b != null && string.Equals(Serialize(a), Serialize(b), StringComparison.Ordinal);
    }
}
