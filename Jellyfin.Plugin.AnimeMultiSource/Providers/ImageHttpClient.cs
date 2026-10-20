using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.AnimeMultiSource.Providers
{
    // Jellyfin proxies remote-search artwork through IRemoteMetadataProvider.GetImageResponse.
    // Without a working implementation the Identify dialog's AniList covers fail to load.
    internal static class ImageHttpClient
    {
        public static readonly HttpClient Instance = Create();

        public static Task<HttpResponseMessage> GetAsync(string url, CancellationToken cancellationToken)
        {
            if (!ImageUrlValidator.IsAllowed(url))
            {
                return Task.FromResult(BadRequest());
            }

            return Instance.GetAsync(url, cancellationToken);
        }

        public static HttpResponseMessage BadRequest()
        {
            return new HttpResponseMessage(HttpStatusCode.BadRequest);
        }

        private static HttpClient Create()
        {
            var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
            };

            var client = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(30)
            };
            client.DefaultRequestHeaders.Add("User-Agent", "Jellyfin-AnimeMultiSource-Plugin/1.0");
            return client;
        }
    }
}
