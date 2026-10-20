using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using MediaBrowser.Model.Providers;

namespace Jellyfin.Plugin.AnimeMultiSource.Providers
{
    // Jellyfin asks the plugin to fetch remote artwork from URLs that originated at third-party
    // APIs. Treat those URLs as untrusted: only download over HTTPS from the image hosts the
    // plugin actually uses, and never a literal private/loopback address.
    internal static class ImageUrlValidator
    {
        private static readonly string[] AllowedHostSuffixes =
        {
            "anilist.co",
            "myanimelist.net",
            "kitsu.io",
            "kitsu.app",
            "thetvdb.com",
            "tmdb.org",
            "fanart.tv"
        };

        public static IEnumerable<RemoteImageInfo> Filter(IEnumerable<RemoteImageInfo> images)
        {
            return images.Where(image => IsAllowed(image.Url));
        }

        public static bool IsAllowed(string? url)
        {
            if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                return false;
            }

            if (uri.Scheme != Uri.UriSchemeHttps)
            {
                return false;
            }

            if (IPAddress.TryParse(uri.Host, out var literal) && IsPrivateOrLocal(literal))
            {
                return false;
            }

            if (uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            foreach (var suffix in AllowedHostSuffixes)
            {
                if (uri.Host.Equals(suffix, StringComparison.OrdinalIgnoreCase) ||
                    uri.Host.EndsWith("." + suffix, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsPrivateOrLocal(IPAddress address)
        {
            if (IPAddress.IsLoopback(address))
            {
                return true;
            }

            if (address.AddressFamily == AddressFamily.InterNetwork)
            {
                var bytes = address.GetAddressBytes();
                return bytes[0] == 10
                    || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
                    || (bytes[0] == 192 && bytes[1] == 168)
                    || (bytes[0] == 169 && bytes[1] == 254)
                    || bytes[0] == 127;
            }

            if (address.AddressFamily == AddressFamily.InterNetworkV6)
            {
                var bytes = address.GetAddressBytes();
                return address.IsIPv6LinkLocal
                    || address.IsIPv6SiteLocal
                    || (bytes[0] & 0xFE) == 0xFC;
            }

            return false;
        }
    }
}
