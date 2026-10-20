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
    public class JikanEpisodeImageProvider : IRemoteImageProvider, IHasOrder
    {
        // Run before Jellyfin's core providers (default order 50) for anime libraries.
        public int Order => 15;

        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);
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
            if (ItemExclusion.IsExcluded(item, Plugin.GetConfigurationSafe(_logger)))
            {
                return Array.Empty<RemoteImageInfo>();
            }

            if (item is not Episode episode || !episode.IndexNumber.HasValue)
            {
                return Array.Empty<RemoteImageInfo>();
            }

            if (ApiService.IsJikanPaused)
            {
                _logger.LogDebug("Skipping Jikan episode image lookup for {Name}: Jikan is paused after repeated failures", episode.Name);
                return Array.Empty<RemoteImageInfo>();
            }

            var malId = GetMalId(episode);
            if (!malId.HasValue || malId.Value <= 0)
            {
                _logger.LogDebug("Episode {Name} has no MAL series ID; skipping Jikan image lookup", episode.Name);
                return Array.Empty<RemoteImageInfo>();
            }

            var context = $"episode image MAL {malId.Value} ep {episode.IndexNumber.Value}";
            var url = $"https://api.jikan.moe/v4/anime/{malId.Value}/episodes/{episode.IndexNumber.Value}";
            try
            {
                await ApiService.EnforceJikanRateLimitAsync(cancellationToken);
                using var response = await GetWithTimeoutAsync(url, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    // 429 is a rate-limit failure, not a success: count it toward the Jikan pause
                    // instead of clearing the failure streak.
                    if ((int)response.StatusCode >= 500 || (int)response.StatusCode == 429)
                    {
                        ApiService.RecordJikanResult(false, context, $"HTTP {(int)response.StatusCode}", _logger);
                    }
                    else
                    {
                        ApiService.RecordJikanResult(true, context, string.Empty, _logger);
                    }

                    _logger.LogDebug("Jikan episode image lookup returned {StatusCode} for MAL {MalId} episode {EpisodeNumber}",
                        response.StatusCode, malId, episode.IndexNumber);
                    return Array.Empty<RemoteImageInfo>();
                }

                ApiService.RecordJikanResult(true, context, string.Empty, _logger);
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
                if (ex is TimeoutException or HttpRequestException)
                {
                    ApiService.RecordJikanResult(false, context, ex.Message, _logger);
                }

                _logger.LogWarning(ex, "Jikan episode image lookup failed for MAL {MalId} episode {EpisodeNumber}", malId, episode.IndexNumber);
                return Array.Empty<RemoteImageInfo>();
            }
        }

        private async Task<HttpResponseMessage> GetWithTimeoutAsync(string url, CancellationToken cancellationToken)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(RequestTimeout);
            try
            {
                return await _httpClient.GetAsync(url, timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                var safeUrl = Uri.TryCreate(url, UriKind.Absolute, out var parsed) ? parsed.GetLeftPart(UriPartial.Path) : "external service";
                throw new TimeoutException($"Request to {safeUrl} timed out after {RequestTimeout.TotalSeconds:F0} s");
            }
        }

        public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
        {
            if (!ImageUrlValidator.IsAllowed(url))
            {
                return Task.FromResult(ImageHttpClient.BadRequest());
            }

            return _httpClient.GetAsync(url, cancellationToken);
        }

        private static int? GetMalId(Episode episode)
        {
            var seasonMalId = episode.Season?.GetProviderId(Constants.MalProviderId);
            if (int.TryParse(seasonMalId, out var parsedSeasonMalId))
            {
                return parsedSeasonMalId;
            }

            if (episode.ParentIndexNumber is 0 or > 1)
            {
                return null;
            }

            var malId = episode.Series?.GetProviderId(Constants.MalProviderId)
                ?? episode.GetProviderId(Constants.MalProviderId);
            return int.TryParse(malId, out var parsedMalId) ? parsedMalId : null;
        }
    }
}