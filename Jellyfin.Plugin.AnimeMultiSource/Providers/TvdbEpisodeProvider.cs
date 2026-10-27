using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AnimeMultiSource.Configuration;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AnimeMultiSource.Providers
{
    public class TvdbEpisodeProvider : IRemoteMetadataProvider<Episode, EpisodeInfo>, IHasOrder
    {
        // Run before Jellyfin's core providers (default order 50) for anime libraries.
        public int Order => 10;

        private readonly ILogger<TvdbEpisodeProvider> _logger;
        private readonly TvdbApiClient _tvdbClient;
        private readonly SeasonEntryResolver _seasonEntryResolver;
        private readonly EntryDetailsLoader _entryDetails;

        public TvdbEpisodeProvider(ILogger<TvdbEpisodeProvider> logger)
        {
            _logger = logger;
            var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
            };

            var httpClient = new HttpClient(handler);
            httpClient.DefaultRequestHeaders.Add("User-Agent", "Jellyfin-AnimeMultiSource-Plugin/1.0");

            _tvdbClient = new TvdbApiClient(httpClient, logger, Constants.TvdbProjectApiKey);

            // The anime APIs get their own client: ApiService adds its own User-Agent header to the one it is given.
            var animeHttpClient = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate });
            var apiService = new ApiService(animeHttpClient, logger);
            _seasonEntryResolver = new SeasonEntryResolver(new AnimeListsApiClient(animeHttpClient, logger), apiService);
            _entryDetails = new EntryDetailsLoader(apiService, logger);
        }

        private static readonly TimeSpan TvdbRequestTimeout = TimeSpan.FromSeconds(15);

        public string Name => $"{Constants.PluginName} TVDB Episodes";

        public async Task<MetadataResult<Episode>> GetMetadata(EpisodeInfo info, CancellationToken cancellationToken)
        {
            var result = new MetadataResult<Episode>();
            if (ItemExclusion.IsExcluded(info.Path, Plugin.GetConfigurationSafe(_logger)))
            {
                return result;
            }

            if (!info.ParentIndexNumber.HasValue || !info.IndexNumber.HasValue)
            {
                _logger.LogDebug("Skipping TVDB lookup for {Name}: missing season/episode numbers", info.Name);
                return result;
            }

            if (!TryGetTvdbSeriesId(info, out var tvdbSeriesId))
            {
                _logger.LogDebug("Skipping TVDB lookup for {Name}: missing TVDB series id", info.Name);
                return result;
            }

            TvdbEpisode? tvdbEpisode;
            try
            {
                using var tvdbTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                tvdbTimeout.CancelAfter(TvdbRequestTimeout);
                tvdbEpisode = await _tvdbClient.FindEpisodeAsync(tvdbSeriesId, info.ParentIndexNumber.Value, info.IndexNumber.Value, tvdbTimeout.Token);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "TVDB episode lookup failed for series {SeriesId} S{Season}E{Episode}", tvdbSeriesId, info.ParentIndexNumber, info.IndexNumber);
                return result;
            }

            if (tvdbEpisode == null)
            {
                _logger.LogWarning("TVDB episode not found for series {SeriesId} S{Season}E{Episode}", tvdbSeriesId, info.ParentIndexNumber, info.IndexNumber);
                return result;
            }

            // Prefer a translation in the library's configured metadata language; fall back to
            // English, then to TVDB's untranslated base fields.
            var preferredLanguage = MapToTvdbLanguageCode(info.MetadataLanguage);
            TvdbTranslation? translation = null;
            try
            {
                using var translationTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                translationTimeout.CancelAfter(TvdbRequestTimeout);
                translation = await _tvdbClient.GetEpisodeTranslationAsync(tvdbEpisode.Id, preferredLanguage, translationTimeout.Token);
                if (translation == null && !string.Equals(preferredLanguage, "eng", StringComparison.OrdinalIgnoreCase))
                {
                    translation = await _tvdbClient.GetEpisodeTranslationAsync(tvdbEpisode.Id, "eng", translationTimeout.Token);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "TVDB translation lookup failed for episode {EpisodeId}; using base fields", tvdbEpisode.Id);
            }

            var episode = new Episode
            {
                Name = translation?.Name ?? tvdbEpisode.Name ?? info.Name,
                OriginalTitle = tvdbEpisode.Name,
                IndexNumber = tvdbEpisode.Number ?? info.IndexNumber,
                ParentIndexNumber = tvdbEpisode.SeasonNumber ?? info.ParentIndexNumber,
                Overview = translation?.Overview ?? tvdbEpisode.Overview
            };

            if (DateTime.TryParse(tvdbEpisode.Aired, out var airDate))
            {
                episode.PremiereDate = airDate;
                episode.ProductionYear = airDate.Year;
            }

            episode.ProviderIds ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            episode.SetProviderId(MetadataProvider.Tvdb, tvdbEpisode.Id.ToString());

            // Parental rating: Jikan, from the anime entry this episode's season belongs to.
            episode.OfficialRating = await GetSeasonRatingAsync(info, cancellationToken);

            result.Item = episode;
            result.HasMetadata = true;

            var imageUrl = TvdbApiClient.NormalizeImageUrl(tvdbEpisode.Image);
            if (imageUrl != null)
            {
                result.RemoteImages = new List<(string Url, ImageType Type)>
                {
                    (imageUrl, ImageType.Primary)
                };
            }

            _logger.LogInformation("TVDB episode metadata set for series {SeriesId} S{Season}E{Episode} - {Title}", tvdbSeriesId, episode.ParentIndexNumber, episode.IndexNumber, episode.Name);

            return result;
        }

        // The rating of the anime entry the episode's season belongs to (Maria Holic season 2 = "Maria Holic Alive"), from
        // Jikan with Kitsu as the fallback. An episode in season 0, or one the API cannot place, takes the series' own entry.
        private async Task<string?> GetSeasonRatingAsync(EpisodeInfo info, CancellationToken cancellationToken)
        {
            try
            {
                if (info.SeriesProviderIds == null
                    || !info.SeriesProviderIds.TryGetValue(Constants.AniListProviderId, out var seriesAniList)
                    || !long.TryParse(seriesAniList, out var seriesId))
                {
                    return null;
                }

                var config = Plugin.GetConfigurationSafe(_logger);
                var entry = info.ParentIndexNumber is int season
                    ? await _seasonEntryResolver.ResolveAsync(config.EffectiveAnimeListsApiUrl, seriesId, season, cancellationToken)
                    : null;

                // Specials (season 0) and seasons the API cannot place take the first season's entry: it has the Kitsu id
                // that keeps the rating available while Jikan is down.
                entry ??= await _seasonEntryResolver.ResolveAsync(config.EffectiveAnimeListsApiUrl, seriesId, 1, cancellationToken);

                return await _entryDetails.GetParentalRatingAsync(entry?.AniListId ?? seriesId, entry?.MalId, entry?.KitsuId);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Could not determine the parental rating for {Name}", info.Name);
                return null;
            }
        }

        public Task<IEnumerable<RemoteSearchResult>> GetSearchResults(EpisodeInfo searchInfo, CancellationToken cancellationToken)
        {
            return Task.FromResult<IEnumerable<RemoteSearchResult>>(Array.Empty<RemoteSearchResult>());
        }

        public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
        {
            if (!ImageUrlValidator.IsAllowed(url))
            {
                return Task.FromResult(ImageHttpClient.BadRequest());
            }

            return _tvdbClient.GetImageAsync(url, cancellationToken);
        }

        // TVDB v4 translations use 3-letter (ISO 639-2/B) language codes; Jellyfin's configured
        // metadata language is a 2-letter (ISO 639-1) code. Covers the languages TVDB commonly
        // has episode translations for; unmapped/unset languages fall back to English.
        private static string MapToTvdbLanguageCode(string? metadataLanguage)
        {
            if (string.IsNullOrWhiteSpace(metadataLanguage))
            {
                return "eng";
            }

            return metadataLanguage.Trim().ToLowerInvariant() switch
            {
                "en" => "eng",
                "fr" => "fra",
                "de" => "deu",
                "es" => "spa",
                "it" => "ita",
                "pt" => "por",
                "ja" => "jpn",
                "ko" => "kor",
                "zh" => "zho",
                "ru" => "rus",
                "nl" => "nld",
                "pl" => "pol",
                "tr" => "tur",
                "ar" => "ara",
                "sv" => "swe",
                "da" => "dan",
                "no" => "nor",
                "fi" => "fin",
                "el" => "ell",
                "he" => "heb",
                "hu" => "hun",
                "cs" => "ces",
                "ro" => "ron",
                "uk" => "ukr",
                _ => "eng"
            };
        }

        private bool TryGetTvdbSeriesId(EpisodeInfo info, out int seriesId)
        {
            seriesId = 0;

            if (info.SeriesProviderIds != null &&
                TryGetValueIgnoreCase(info.SeriesProviderIds, MetadataProvider.Tvdb.ToString(), out var rawSeriesId) &&
                int.TryParse(rawSeriesId, out seriesId))
            {
                return true;
            }

            if (info.ProviderIds != null &&
                TryGetValueIgnoreCase(info.ProviderIds, MetadataProvider.Tvdb.ToString(), out rawSeriesId) &&
                int.TryParse(rawSeriesId, out seriesId))
            {
                return true;
            }

            return false;
        }

        private bool TryGetValueIgnoreCase(IDictionary<string, string> dictionary, string key, out string value)
        {
            foreach (var kvp in dictionary)
            {
                if (kvp.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
                {
                    value = kvp.Value;
                    return true;
                }
            }

            value = string.Empty;
            return false;
        }
    }
}
