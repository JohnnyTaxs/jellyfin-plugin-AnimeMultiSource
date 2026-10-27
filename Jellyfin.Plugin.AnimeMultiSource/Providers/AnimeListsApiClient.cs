using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AnimeMultiSource.Providers
{
    /// <summary>
    /// Client for the anime-lists API (title/synonym search that returns every known id for an anime).
    /// Opt-in: only used when the user has switched on <c>UseAnimeListsApi</c>.
    /// </summary>
    public sealed class AnimeListsApiClient
    {
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan PauseDuration = TimeSpan.FromMinutes(5);
        private const int FailuresBeforePause = 3;
        private static readonly object HealthLock = new();
        private static int _consecutiveFailures;
        private static DateTimeOffset _pausedUntil = DateTimeOffset.MinValue;

        private readonly HttpClient _httpClient;
        private readonly ILogger _logger;

        public AnimeListsApiClient(HttpClient httpClient, ILogger logger)
        {
            _httpClient = httpClient;
            _logger = logger;
        }

        /// <summary>
        /// Searches by title (and year when known). Returns null when the API is unreachable or paused after repeated
        /// failures, so the caller can fall back to another source; an empty list means "reachable, nothing found".
        /// </summary>
        public async Task<IReadOnlyList<AnimeCandidate>?> SearchAsync(
            string baseUrl,
            string query,
            int? year,
            bool movie,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(query))
            {
                return null;
            }

            var url = baseUrl.TrimEnd('/')
                + "/v1/search?q=" + Uri.EscapeDataString(query)
                + (year.HasValue ? "&year=" + year.Value : string.Empty)
                + "&media=" + (movie ? "movie" : "series")
                + "&verify=false&limit=8";

            return await GetCandidatesAsync(url, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Looks an entry up by one known id (<c>anilist_id</c>, <c>mal_id</c>, <c>imdb_id</c>, <c>tvdb_id</c>, ...) and
        /// returns every id the API has for it. Same null / empty convention as <see cref="SearchAsync"/>.
        /// </summary>
        public async Task<IReadOnlyList<AnimeCandidate>?> LookupAsync(
            string baseUrl,
            string idName,
            string idValue,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(idName) || string.IsNullOrWhiteSpace(idValue))
            {
                return null;
            }

            var url = baseUrl.TrimEnd('/')
                + "/v1/lookup?" + idName + "=" + Uri.EscapeDataString(idValue)
                + "&fill=false&verify=false";

            return await GetCandidatesAsync(url, cancellationToken).ConfigureAwait(false);
        }

        private Task<IReadOnlyList<AnimeCandidate>?> GetCandidatesAsync(string url, CancellationToken cancellationToken) =>
            GetJsonAsync<IReadOnlyList<AnimeCandidate>>(url, RequestTimeout, root => Parse(root), new List<AnimeCandidate>(), cancellationToken);

        /// <summary>
        /// The franchise (TVDB/TMDb series) an AniList entry belongs to, with its seasons and the anime entries behind each
        /// season. A season is marked <see cref="ApiSeason.Inferred"/> when one long entry (Dragon Ball Z: 291 episodes) had
        /// to be split to match the TVDB/TMDb seasons. Null when the API is unreachable or paused; an empty franchise
        /// (<see cref="ApiFranchise.Seasons"/> empty) when it does not know the entry.
        /// </summary>
        public async Task<ApiFranchise?> GetFranchiseAsync(string baseUrl, long aniListId, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(baseUrl) || aniListId <= 0)
            {
                return null;
            }

            if (_franchiseCache.TryGetValue(aniListId, out var cached) && DateTimeOffset.UtcNow - cached.At < FranchiseCacheDuration)
            {
                return cached.Franchise;
            }

            var url = baseUrl.TrimEnd('/') + "/v1/series/anilist/" + aniListId + "?verify=false";
            var franchise = await GetJsonAsync<ApiFranchise>(url, SeriesRequestTimeout, ParseFranchise, new ApiFranchise(), cancellationToken).ConfigureAwait(false);
            if (franchise != null)
            {
                _franchiseCache[aniListId] = (DateTimeOffset.UtcNow, franchise);
            }

            return franchise;
        }

        /// <summary>
        /// Shows (TVDB/TMDb series) matching a title, best first - what the Identify dialog lists. Same null / empty
        /// convention as <see cref="SearchAsync"/>.
        /// </summary>
        public Task<IReadOnlyList<ApiFranchise>?> SearchSeriesAsync(string baseUrl, string query, int? year, int limit, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(query))
            {
                return Task.FromResult<IReadOnlyList<ApiFranchise>?>(null);
            }

            var url = baseUrl.TrimEnd('/')
                + "/v1/series?q=" + Uri.EscapeDataString(query)
                + (year.HasValue ? "&year=" + year.Value : string.Empty)
                + "&limit=" + limit + "&verify=false";

            return GetJsonAsync<IReadOnlyList<ApiFranchise>>(url, SeriesRequestTimeout, root => ParseFranchises(root), new List<ApiFranchise>(), cancellationToken);
        }

        /// <summary>The show an entry belongs to, looked up by an id on another platform ("anilist" or "mal"). Not cached.</summary>
        public async Task<ApiFranchise?> GetFranchiseByPlatformAsync(string baseUrl, string platform, long id, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(platform) || id <= 0)
            {
                return null;
            }

            var url = baseUrl.TrimEnd('/') + "/v1/series/" + platform + "/" + id + "?verify=false";
            return await GetJsonAsync<ApiFranchise>(url, SeriesRequestTimeout, ParseFranchise, new ApiFranchise(), cancellationToken).ConfigureAwait(false);
        }

        private static readonly ConcurrentDictionary<long, (DateTimeOffset At, ApiFranchise Franchise)> _franchiseCache = new();
        private static readonly TimeSpan FranchiseCacheDuration = TimeSpan.FromHours(12);
        private static readonly TimeSpan SeriesRequestTimeout = TimeSpan.FromSeconds(60);

        private async Task<T?> GetJsonAsync<T>(string url, TimeSpan requestTimeout, Func<JsonElement, T> parse, T notFound, CancellationToken cancellationToken)
            where T : class
        {
            lock (HealthLock)
            {
                if (DateTimeOffset.UtcNow < _pausedUntil)
                {
                    _logger.LogDebug("Skipping anime-lists API: paused after repeated failures");
                    return null;
                }
            }

            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(requestTimeout);

                using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    // The API answers an id it does not know with 404: reachable, nothing found.
                    RecordResult(success: true, reason: string.Empty);
                    return notFound;
                }

                response.EnsureSuccessStatusCode();

                // Read as a stream: System.Text.Json always decodes UTF-8, whatever charset the server declares.
                await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token).ConfigureAwait(false);

                RecordResult(success: true, reason: string.Empty);
                return parse(document.RootElement);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException)
            {
                RecordResult(success: false, reason: ex is OperationCanceledException ? "timeout" : ex.Message);
                return null;
            }
        }

        // /v1/series/{platform}/{id} answers with the franchise the entry belongs to: take the first.
        private static ApiFranchise ParseFranchise(JsonElement root) =>
            ParseFranchises(root).FirstOrDefault() ?? new ApiFranchise();

        // /v1/series?q= answers with every franchise matching the title.
        private static List<ApiFranchise> ParseFranchises(JsonElement root)
        {
            var franchises = new List<ApiFranchise>();
            if (root.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in results.EnumerateArray())
                {
                    franchises.Add(ParseFranchiseItem(item));
                }
            }

            return franchises;
        }

        private static ApiFranchise ParseFranchiseItem(JsonElement item)
        {
            var franchise = new ApiFranchise
            {
                Title = GetString(item, "title"),
                Year = (int?)GetLong(item, "year"),
                MatchedSeason = (int?)GetLong(item, "matched_season")
            };

            if (item.TryGetProperty("ids", out var ids) && ids.ValueKind == JsonValueKind.Object)
            {
                franchise.TvdbId = GetLong(ids, "tvdb_id");
                franchise.TmdbId = GetLong(ids, "themoviedb_id");
            }

            if (item.TryGetProperty("seasons", out var seasons) && seasons.ValueKind == JsonValueKind.Array)
            {
                foreach (var seasonElement in seasons.EnumerateArray())
                {
                    var season = new ApiSeason
                    {
                        Number = (int?)GetLong(seasonElement, "season") ?? -1,
                        Episodes = (int?)GetLong(seasonElement, "episodes"),
                        Inferred = seasonElement.TryGetProperty("inferred", out var inferred) && inferred.ValueKind == JsonValueKind.True
                    };

                    if (seasonElement.TryGetProperty("entries", out var entries) && entries.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var entry in entries.EnumerateArray())
                        {
                            season.Entries.Add(ParseItem(entry));
                        }
                    }

                    if (season.Number >= 0)
                    {
                        franchise.Seasons.Add(season);
                    }
                }
            }

            return franchise;
        }

        private void RecordResult(bool success, string reason)
        {
            lock (HealthLock)
            {
                if (success)
                {
                    _consecutiveFailures = 0;
                    return;
                }

                _consecutiveFailures++;
                if (_consecutiveFailures < FailuresBeforePause)
                {
                    _logger.LogWarning("anime-lists API request failed ({Reason}); {Count}/{Max} before pausing it", reason, _consecutiveFailures, FailuresBeforePause);
                    return;
                }

                _pausedUntil = DateTimeOffset.UtcNow + PauseDuration;
                _consecutiveFailures = 0;
                _logger.LogWarning("anime-lists API failed {Max} times in a row (last: {Reason}); not calling it again until {Until:HH:mm}; items it would have identified are skipped and tried on their next refresh",
                    FailuresBeforePause, reason, _pausedUntil.ToLocalTime());
            }
        }

        private static List<AnimeCandidate> Parse(JsonElement root)
        {
            var candidates = new List<AnimeCandidate>();
            if (!root.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
            {
                return candidates;
            }

            foreach (var item in results.EnumerateArray())
            {
                candidates.Add(ParseItem(item));
            }

            return candidates;
        }

        // One anime entry as the API describes it (a search/lookup result, or an entry inside a franchise season).
        private static AnimeCandidate ParseItem(JsonElement item)
        {
            var candidate = new AnimeCandidate { Source = "api" };

            if (item.TryGetProperty("ids", out var ids) && ids.ValueKind == JsonValueKind.Object)
            {
                candidate.AniListId = GetLong(ids, "anilist_id");
                candidate.MalId = GetLong(ids, "mal_id");
                candidate.AniDbId = GetLong(ids, "anidb_id");
                candidate.KitsuId = GetLong(ids, "kitsu_id");
                candidate.AniSearchId = GetLong(ids, "anisearch_id");
                candidate.TmdbId = GetLong(ids, "themoviedb_id");
                if (ids.TryGetProperty("imdb_id", out var imdb) && imdb.ValueKind == JsonValueKind.Array)
                {
                    foreach (var value in imdb.EnumerateArray())
                    {
                        if (value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()))
                        {
                            candidate.ImdbIds.Add(value.GetString()!);
                        }
                    }
                }
            }

            if (item.TryGetProperty("meta", out var meta) && meta.ValueKind == JsonValueKind.Object)
            {
                candidate.Title = GetString(meta, "title");
                candidate.Type = GetString(meta, "format") ?? GetString(meta, "type");
                candidate.Year = (int?)GetLong(meta, "year");
                candidate.Duration = (int?)GetLong(meta, "duration");
            }

            if (item.TryGetProperty("titles", out var titles) && titles.ValueKind == JsonValueKind.Object)
            {
                candidate.English = GetString(titles, "english");
                candidate.Romaji = GetString(titles, "romaji");
                candidate.Native = GetString(titles, "native");
                if (titles.TryGetProperty("synonyms", out var synonyms) && synonyms.ValueKind == JsonValueKind.Array)
                {
                    foreach (var synonym in synonyms.EnumerateArray())
                    {
                        if (synonym.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(synonym.GetString()))
                        {
                            candidate.Synonyms.Add(synonym.GetString()!);
                        }
                    }
                }
            }

            if (item.TryGetProperty("match", out var match) && match.ValueKind == JsonValueKind.Object)
            {
                candidate.ApiTitleMatch = GetString(match, "title");
                candidate.ApiYearMatch = GetString(match, "year");
            }

            return candidate;
        }

        private static long? GetLong(JsonElement element, string name)
        {
            if (!element.TryGetProperty(name, out var value))
            {
                return null;
            }

            return value.ValueKind switch
            {
                JsonValueKind.Number when value.TryGetInt64(out var number) => number,
                JsonValueKind.String when long.TryParse(value.GetString(), out var parsed) => parsed,
                _ => null
            };
        }

        private static string? GetString(JsonElement element, string name)
        {
            if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
            {
                var text = value.GetString();
                return string.IsNullOrWhiteSpace(text) ? null : text;
            }

            return null;
        }
    }
}
