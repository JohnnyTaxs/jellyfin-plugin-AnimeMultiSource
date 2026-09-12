using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AnimeMultiSource.Providers
{
    public class JikanEpisodeImageProvider : IRemoteImageProvider
    {
        private readonly ILogger<JikanEpisodeImageProvider> _logger;
        private readonly HttpClient _httpClient;

        public JikanEpisodeImageProvider(ILogger<JikanEpisodeImageProvider> logger)
        {
            _logger = logger;
            var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
            };
            _httpClient = new HttpClient(handler);
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "Jellyfin-AnimeMultiSource-Plugin/1.0");
        }

        public string Name => $"{Constants.PluginName} Jikan Episode Images";

        public bool Supports(BaseItem item)
        {
            return item is Episode;
        }

        public IEnumerable<ImageType> GetSupportedImages(BaseItem item)
        {
            yield return ImageType.Primary;
        }

        public async Task<IEnumerable<RemoteImageInfo>> GetImages(BaseItem item, CancellationToken cancellationToken)
        {
            if (item is not Episode episode || !episode.IndexNumber.HasValue)
            {
                return Array.Empty<RemoteImageInfo>();
            }

            var malId = GetMalId(episode);
            if (!malId.HasValue || malId.Value <= 0)
            {
                _logger.LogDebug("Episode {Name} has no MAL series ID; skipping Jikan image lookup", episode.Name);
                return Array.Empty<RemoteImageInfo>();
            }

            var url = $"https://api.jikan.moe/v4/anime/{malId.Value}/episodes/{episode.IndexNumber.Value}";
            try
            {
                using var response = await _httpClient.GetAsync(url, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogDebug("Jikan episode image lookup returned {StatusCode} for MAL {MalId} episode {EpisodeNumber}",
                        response.StatusCode, malId, episode.IndexNumber);
                    return Array.Empty<RemoteImageInfo>();
                }

                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                var data = JsonSerializer.Deserialize<JikanEpisodeResponse>(json)?.Data;
                var imageUrl = data?.Images?.WebP?.ImageUrl ?? data?.Images?.Jpg?.ImageUrl;
                if (string.IsNullOrWhiteSpace(imageUrl))
                {
                    _logger.LogDebug("Jikan has no image for MAL {MalId} episode {EpisodeNumber}", malId, episode.IndexNumber);
                    return Array.Empty<RemoteImageInfo>();
                }

                _logger.LogInformation("Returning Jikan episode image for {Name}: {Url}", episode.Name, imageUrl);
                return new[]
                {
                    new RemoteImageInfo
                    {
                        ProviderName = Name,
                        Url = imageUrl,
                        Type = ImageType.Primary
                    }
                };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Jikan episode image lookup failed for MAL {MalId} episode {EpisodeNumber}", malId, episode.IndexNumber);
                return Array.Empty<RemoteImageInfo>();
            }
        }

        public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
        {
            return _httpClient.GetAsync(url, cancellationToken);
        }

        private static int? GetMalId(Episode episode)
        {
            var malId = episode.Series?.GetProviderId(Constants.MalProviderId)
                ?? episode.GetProviderId(Constants.MalProviderId);
            return int.TryParse(malId, out var parsedMalId) ? parsedMalId : null;
        }
    }
}