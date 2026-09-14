using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AnimeMultiSource.Providers;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AnimeMultiSource.Providers
{
    public class TvdbEpisodeImageProvider : IRemoteImageProvider
    {
        private readonly ILogger<TvdbEpisodeImageProvider> _logger;
        private readonly TvdbApiClient _tvdbClient;
        private readonly HttpClient _jikanClient;
        private readonly AnimeListMapper _animeListMapper;
        private readonly PlexMatchParser _plexMatchParser;
        private static readonly object TmdbRateLock = new();
        private static DateTimeOffset _lastTmdbRequest = DateTimeOffset.MinValue;
        private static readonly TimeSpan TmdbMinimumRequestSpacing = TimeSpan.FromMilliseconds(250);
        private static readonly Regex EpisodeNumberPattern = new(
            @"(?:^|[ ._-])S(?<season>\d{1,2})E(?<episode>\d{1,3})(?:[^0-9]|$)|(?:^|[ ._-])(?<season2>\d{1,2})x(?<episode2>\d{1,3})(?:[^0-9]|$)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public TvdbEpisodeImageProvider(ILogger<TvdbEpisodeImageProvider> logger)
        {
            _logger = logger;

            var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
            };

            var httpClient = new HttpClient(handler);
            httpClient.DefaultRequestHeaders.Add("User-Agent", "Jellyfin-AnimeMultiSource-Plugin/1.0");

            _tvdbClient = new TvdbApiClient(httpClient, logger, Constants.TvdbProjectApiKey);
            _jikanClient = new HttpClient(handler);
            _jikanClient.DefaultRequestHeaders.Add("User-Agent", "Jellyfin-AnimeMultiSource-Plugin/1.0");
            _animeListMapper = new AnimeListMapper(_jikanClient, logger);
            _plexMatchParser = new PlexMatchParser(logger);
        }

        public string Name => $"{Constants.PluginName} TVDB Episode Images";

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
            if (item is not Episode episode)
            {
                return Array.Empty<RemoteImageInfo>();
            }

            var tvdbId = episode.GetProviderId(MetadataProvider.Tvdb);
            if (string.IsNullOrWhiteSpace(tvdbId) || !int.TryParse(tvdbId, out var episodeId))
            {
                _logger.LogInformation("Episode {Name} missing TVDB provider id; trying Jikan/MAL and Kitsu", episode.Name);
                return await GetFallbackImagesAsync(episode, cancellationToken);
            }

            var tvdbEpisode = await _tvdbClient.GetEpisodeByIdAsync(episodeId, cancellationToken);
            var imageUrl = TvdbApiClient.NormalizeImageUrl(tvdbEpisode?.Image);
            if (imageUrl != null)
            {
                _logger.LogInformation("Returning TVDB episode image for {Name}: {Url}", episode.Name, imageUrl);
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

            _logger.LogInformation("TVDB has no image for episode {EpisodeId}; trying Jikan/MAL", episodeId);
            return await GetFallbackImagesAsync(episode, cancellationToken);
        }

        private async Task<IEnumerable<RemoteImageInfo>> GetFallbackImagesAsync(Episode episode, CancellationToken cancellationToken)
        {
            var jikanImages = await GetJikanImageAsync(episode, cancellationToken);
            if (jikanImages.Any())
            {
                return jikanImages;
            }

            _logger.LogInformation("Jikan has no usable image for {Name}; trying Kitsu", episode.Name);
            var kitsuImages = await GetKitsuImageAsync(episode, cancellationToken);
            if (kitsuImages.Any())
            {
                return kitsuImages;
            }

            _logger.LogInformation("Kitsu has no usable image for {Name}; trying TMDB", episode.Name);
            return await GetTmdbImageAsync(episode, cancellationToken);
        }

        private async Task<IEnumerable<RemoteImageInfo>> GetJikanImageAsync(Episode episode, CancellationToken cancellationToken)
        {
            var malId = await ResolveMalIdAsync(episode, cancellationToken);
            var numbers = GetEpisodeNumbers(episode);
            if (!malId.HasValue || !numbers.Episode.HasValue)
            {
                _logger.LogInformation("No MAL ID available for {Name}; skipping Jikan image lookup", episode.Name);
                return Array.Empty<RemoteImageInfo>();
            }

            var url = $"https://api.jikan.moe/v4/anime/{malId.Value}/episodes/{numbers.Episode.Value}";
            try
            {
                using var response = await _jikanClient.GetAsync(url, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogInformation("Jikan returned {StatusCode} for MAL {MalId} episode {EpisodeNumber}", response.StatusCode, malId, numbers.Episode);
                    return Array.Empty<RemoteImageInfo>();
                }

                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                var data = JsonSerializer.Deserialize<JikanEpisodeResponse>(json)?.Data;
                var jikanImage = data?.Images?.WebP?.ImageUrl ?? data?.Images?.Jpg?.ImageUrl;
                if (string.IsNullOrWhiteSpace(jikanImage))
                {
                    _logger.LogInformation("Jikan has no image for MAL {MalId} episode {EpisodeNumber}", malId, numbers.Episode);
                    return Array.Empty<RemoteImageInfo>();
                }

                _logger.LogInformation("Returning Jikan episode image for {Name}: {Url}", episode.Name, jikanImage);
                return new[]
                {
                    new RemoteImageInfo
                    {
                        ProviderName = Name,
                        Url = jikanImage,
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
                _logger.LogWarning(ex, "Jikan image lookup failed for {Name}", episode.Name);
                return Array.Empty<RemoteImageInfo>();
            }
        }

        private async Task<long?> ResolveMalIdAsync(Episode episode, CancellationToken cancellationToken)
        {
            var rawMalId = episode.Series?.GetProviderId(Constants.MalProviderId)
                ?? episode.GetProviderId(Constants.MalProviderId);
            if (long.TryParse(rawMalId, out var malId))
            {
                return malId;
            }

            var seriesPath = ResolveSeriesPath(episode);
            if (string.IsNullOrWhiteSpace(seriesPath))
            {
                return null;
            }

            var plexMatchPath = Path.Combine(seriesPath, Constants.PlexMatchFileName);
            if (!File.Exists(plexMatchPath))
            {
                return null;
            }

            try
            {
                var plexData = _plexMatchParser.ParsePlexMatch(await File.ReadAllTextAsync(plexMatchPath, cancellationToken));
                if (plexData.MalId.HasValue)
                {
                    _logger.LogInformation("Resolved MAL ID {MalId} directly from {Path}", plexData.MalId, plexMatchPath);
                    return plexData.MalId.Value;
                }

                await _animeListMapper.LoadAnimeListsAsync();
                AnimeMapping? mapping = null;
                if (!string.IsNullOrWhiteSpace(plexData.TvdbId))
                {
                    mapping = _animeListMapper.GetMappingByTvdbId(plexData.TvdbId);
                }

                if (mapping == null && !string.IsNullOrWhiteSpace(plexData.ImdbId))
                {
                    mapping = _animeListMapper.GetMappingByImdbId(plexData.ImdbId);
                }

                if (mapping == null && plexData.AniListId.HasValue)
                {
                    mapping = _animeListMapper.GetMappingByAniListId(plexData.AniListId.Value);
                }

                return mapping?.mal_id;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to resolve MAL ID from .plexmatch for {Path}", plexMatchPath);
                return null;
            }
        }

        private async Task<IEnumerable<RemoteImageInfo>> GetKitsuImageAsync(Episode episode, CancellationToken cancellationToken)
        {
            var kitsuId = await ResolveKitsuIdAsync(episode, cancellationToken);
            var numbers = GetEpisodeNumbers(episode);
            if (string.IsNullOrWhiteSpace(kitsuId) || !numbers.Episode.HasValue)
            {
                _logger.LogInformation("No Kitsu ID available for {Name}; skipping Kitsu image lookup", episode.Name);
                return Array.Empty<RemoteImageInfo>();
            }

            try
            {
                const int pageSize = 20;
                for (var offset = 0; offset <= 1000; offset += pageSize)
                {
                    var url = $"https://kitsu.io/api/edge/anime/{Uri.EscapeDataString(kitsuId)}/episodes?page%5Blimit%5D={pageSize}&page%5Boffset%5D={offset}";
                    using var response = await _jikanClient.GetAsync(url, cancellationToken);
                    if (!response.IsSuccessStatusCode)
                    {
                        _logger.LogInformation("Kitsu returned {StatusCode} for anime {KitsuId} at offset {Offset}", response.StatusCode, kitsuId, offset);
                        return Array.Empty<RemoteImageInfo>();
                    }

                    var json = await response.Content.ReadAsStringAsync(cancellationToken);
                    var episodes = JsonSerializer.Deserialize<KitsuEpisodeResponse>(json)?.Data;
                    var kitsuEpisode = episodes?.FirstOrDefault(candidate =>
                        candidate.Attributes?.Number == numbers.Episode &&
                        (!numbers.Season.HasValue || candidate.Attributes.SeasonNumber == numbers.Season));
                    var imageUrl = kitsuEpisode?.Attributes?.Thumbnail?.Original;
                    if (!string.IsNullOrWhiteSpace(imageUrl))
                    {
                        _logger.LogInformation("Returning Kitsu episode image for {Name}: {Url}", episode.Name, imageUrl);
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

                    if (episodes == null || episodes.Count < pageSize)
                    {
                        break;
                    }
                }

                _logger.LogInformation("Kitsu has no image for anime {KitsuId} episode {EpisodeNumber}", kitsuId, numbers.Episode);
                return Array.Empty<RemoteImageInfo>();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Kitsu image lookup failed for {Name}", episode.Name);
                return Array.Empty<RemoteImageInfo>();
            }
        }

        private async Task<string?> ResolveKitsuIdAsync(Episode episode, CancellationToken cancellationToken)
        {
            var providerId = episode.Series?.GetProviderId(Constants.KitsuProviderId)
                ?? episode.GetProviderId(Constants.KitsuProviderId);
            if (!string.IsNullOrWhiteSpace(providerId))
            {
                return providerId;
            }

            var seriesPath = ResolveSeriesPath(episode);
            if (string.IsNullOrWhiteSpace(seriesPath))
            {
                return null;
            }

            var plexMatchPath = Path.Combine(seriesPath, Constants.PlexMatchFileName);
            if (!File.Exists(plexMatchPath))
            {
                return null;
            }

            try
            {
                var plexData = _plexMatchParser.ParsePlexMatch(await File.ReadAllTextAsync(plexMatchPath, cancellationToken));
                await _animeListMapper.LoadAnimeListsAsync();
                AnimeMapping? mapping = null;
                if (!string.IsNullOrWhiteSpace(plexData.TvdbId))
                {
                    mapping = _animeListMapper.GetMappingByTvdbId(plexData.TvdbId);
                }

                if (mapping == null && !string.IsNullOrWhiteSpace(plexData.ImdbId))
                {
                    mapping = _animeListMapper.GetMappingByImdbId(plexData.ImdbId);
                }

                if (mapping == null && plexData.AniListId.HasValue)
                {
                    mapping = _animeListMapper.GetMappingByAniListId(plexData.AniListId.Value);
                }

                return mapping?.kitsu_id?.ToString();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to resolve Kitsu ID from .plexmatch for {Path}", plexMatchPath);
                return null;
            }
        }

        private async Task<IEnumerable<RemoteImageInfo>> GetTmdbImageAsync(Episode episode, CancellationToken cancellationToken)
        {
            var config = Plugin.GetConfigurationSafe(_logger);
            if (string.IsNullOrWhiteSpace(config.TmdbApiKey))
            {
                _logger.LogInformation("TMDB API key is not configured; skipping TMDB image lookup for {Name}", episode.Name);
                return Array.Empty<RemoteImageInfo>();
            }

            var tmdbId = await ResolveTmdbIdAsync(episode, cancellationToken);
            var numbers = GetEpisodeNumbers(episode);
            if (!tmdbId.HasValue || !numbers.Season.HasValue || !numbers.Episode.HasValue)
            {
                _logger.LogInformation("No TMDB ID or season/episode number available for {Name}; skipping TMDB image lookup", episode.Name);
                return Array.Empty<RemoteImageInfo>();
            }

            var url = $"https://api.themoviedb.org/3/tv/{tmdbId.Value}/season/{numbers.Season.Value}/episode/{numbers.Episode.Value}/images?api_key={Uri.EscapeDataString(config.TmdbApiKey)}";
            try
            {
                using var response = await SendTmdbRequestAsync(url, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogInformation("TMDB returned {StatusCode} for show {TmdbId} S{Season}E{Episode}", response.StatusCode, tmdbId, numbers.Season, numbers.Episode);
                    return Array.Empty<RemoteImageInfo>();
                }

                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
                if (!document.RootElement.TryGetProperty("stills", out var stills) || stills.ValueKind != JsonValueKind.Array)
                {
                    _logger.LogInformation("TMDB has no stills for show {TmdbId} S{Season}E{Episode}", tmdbId, numbers.Season, numbers.Episode);
                    return Array.Empty<RemoteImageInfo>();
                }

                foreach (var still in stills.EnumerateArray())
                {
                    if (still.TryGetProperty("file_path", out var filePath) && !string.IsNullOrWhiteSpace(filePath.GetString()))
                    {
                        var imageUrl = $"https://image.tmdb.org/t/p/original{filePath.GetString()}";
                        _logger.LogInformation("Returning TMDB episode image for {Name}: {Url}", episode.Name, imageUrl);
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
                }

                return Array.Empty<RemoteImageInfo>();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "TMDB image lookup failed for {Name}", episode.Name);
                return Array.Empty<RemoteImageInfo>();
            }
        }

        private async Task<long?> ResolveTmdbIdAsync(Episode episode, CancellationToken cancellationToken)
        {
            var providerId = episode.Series?.GetProviderId(MetadataProvider.Tmdb)
                ?? episode.GetProviderId(MetadataProvider.Tmdb);
            if (long.TryParse(providerId, out var tmdbId))
            {
                return tmdbId;
            }

            var seriesPath = ResolveSeriesPath(episode);
            if (string.IsNullOrWhiteSpace(seriesPath))
            {
                return null;
            }

            var plexMatchPath = Path.Combine(seriesPath, Constants.PlexMatchFileName);
            if (!File.Exists(plexMatchPath))
            {
                return null;
            }

            try
            {
                var plexData = _plexMatchParser.ParsePlexMatch(await File.ReadAllTextAsync(plexMatchPath, cancellationToken));
                if (plexData.TmdbId.HasValue)
                {
                    return plexData.TmdbId.Value;
                }

                await _animeListMapper.LoadAnimeListsAsync();
                AnimeMapping? mapping = null;
                if (!string.IsNullOrWhiteSpace(plexData.TvdbId))
                {
                    mapping = _animeListMapper.GetMappingByTvdbId(plexData.TvdbId);
                }

                if (mapping == null && !string.IsNullOrWhiteSpace(plexData.ImdbId))
                {
                    mapping = _animeListMapper.GetMappingByImdbId(plexData.ImdbId);
                }

                if (mapping == null && plexData.AniListId.HasValue)
                {
                    mapping = _animeListMapper.GetMappingByAniListId(plexData.AniListId.Value);
                }

                return mapping?.PreferredTmdbId;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to resolve TMDB ID from .plexmatch for {Path}", plexMatchPath);
                return null;
            }
        }

        private static (int? Season, int? Episode) GetEpisodeNumbers(Episode episode)
        {
            var season = episode.ParentIndexNumber;
            var episodeNumber = episode.IndexNumber;
            if (season.HasValue && episodeNumber.HasValue)
            {
                return (season, episodeNumber);
            }

            var source = episode.Path ?? episode.Name;
            var match = EpisodeNumberPattern.Match(source ?? string.Empty);
            if (!match.Success)
            {
                return (season, episodeNumber);
            }

            if (!season.HasValue)
            {
                var seasonText = match.Groups["season"].Success
                    ? match.Groups["season"].Value
                    : match.Groups["season2"].Value;
                if (int.TryParse(seasonText, out var parsedSeason))
                {
                    season = parsedSeason;
                }
            }

            if (!episodeNumber.HasValue)
            {
                var episodeText = match.Groups["episode"].Success
                    ? match.Groups["episode"].Value
                    : match.Groups["episode2"].Value;
                if (int.TryParse(episodeText, out var parsedEpisode))
                {
                    episodeNumber = parsedEpisode;
                }
            }

            return (season, episodeNumber);
        }

        private static string? ResolveSeriesPath(Episode episode)
        {
            if (!string.IsNullOrWhiteSpace(episode.Series?.Path))
            {
                return episode.Series.Path;
            }

            if (string.IsNullOrWhiteSpace(episode.Path))
            {
                return null;
            }

            var episodeDirectory = Directory.Exists(episode.Path)
                ? episode.Path
                : Path.GetDirectoryName(episode.Path);
            if (string.IsNullOrWhiteSpace(episodeDirectory))
            {
                return null;
            }

            var seasonDirectoryName = Path.GetFileName(episodeDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            return SeasonNumberParser.TryParse(seasonDirectoryName).HasValue
                ? Directory.GetParent(episodeDirectory)?.FullName
                : episodeDirectory;
        }

        private async Task<HttpResponseMessage> SendTmdbRequestAsync(string url, CancellationToken cancellationToken)
        {
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                await EnforceTmdbRateLimitAsync(cancellationToken);
                var response = await _jikanClient.GetAsync(url, cancellationToken);
                if (response.StatusCode != System.Net.HttpStatusCode.TooManyRequests || attempt == 3)
                {
                    return response;
                }

                var retryAfter = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(2);
                response.Dispose();
                _logger.LogWarning("TMDB rate limited request; retrying in {DelayMs} ms (attempt {Attempt})", retryAfter.TotalMilliseconds, attempt);
                await Task.Delay(retryAfter, cancellationToken);
            }

            throw new InvalidOperationException("TMDB request retry loop exited unexpectedly.");
        }

        private static async Task EnforceTmdbRateLimitAsync(CancellationToken cancellationToken)
        {
            TimeSpan delay;
            lock (TmdbRateLock)
            {
                var now = DateTimeOffset.UtcNow;
                var nextAllowed = _lastTmdbRequest + TmdbMinimumRequestSpacing;
                delay = nextAllowed > now ? nextAllowed - now : TimeSpan.Zero;
                _lastTmdbRequest = now + delay;
            }

            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken);
            }
        }

        public async Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
        {
            var normalizedUrl = TvdbApiClient.NormalizeImageUrl(url);
            if (normalizedUrl == null)
            {
                throw new InvalidOperationException("TVDB returned an invalid episode image URL.");
            }

            var response = await _tvdbClient.GetImageAsync(normalizedUrl, cancellationToken);
            _logger.LogInformation("TVDB episode image download returned {StatusCode} for {Url}", response.StatusCode, normalizedUrl);
            return response;
        }
    }
}
