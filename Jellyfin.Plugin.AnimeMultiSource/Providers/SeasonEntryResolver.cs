using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.AnimeMultiSource.Providers
{
    /// <summary>The anime entry behind one season of a series.</summary>
    public sealed class SeasonEntry
    {
        public long? AniListId { get; set; }

        public long? MalId { get; set; }

        public long? KitsuId { get; set; }

        /// <summary>True when one long entry was split to fit this season (Dragon Ball Z's nine sagas).</summary>
        public bool Inferred { get; set; }

        /// <summary>
        /// True when the entry's own title, overview and ids describe this season. A split season shares an entry with its
        /// neighbours, so only the first of them owns it; the others still use it for rating, genres, studios and people.
        /// </summary>
        public bool OwnsEntry { get; set; }

        /// <summary>The anime-lists API named the entry (as opposed to AniList's sequel chain).</summary>
        public bool FromApi { get; set; }
    }

    /// <summary>
    /// Season N of "Maria Holic" is the entry "Maria Holic Alive". The anime-lists API says which entry that is the way
    /// TVDB/TMDb number it; only when it has no answer is AniList's sequel chain followed.
    /// </summary>
    public sealed class SeasonEntryResolver
    {
        private static readonly ConcurrentDictionary<(long Series, int Season), (DateTimeOffset At, SeasonEntry? Entry)> Cache = new();
        private static readonly TimeSpan CacheDuration = TimeSpan.FromHours(6);
        private static readonly TimeSpan MissCacheDuration = TimeSpan.FromMinutes(10);

        private readonly AnimeListsApiClient _apiClient;
        private readonly ApiService _apiService;

        public SeasonEntryResolver(AnimeListsApiClient apiClient, ApiService apiService)
        {
            _apiClient = apiClient;
            _apiService = apiService;
        }

        /// <summary>Pure: the entry an API franchise names for a season, or null when it does not have that season.</summary>
        public static SeasonEntry? FromFranchise(ApiFranchise? franchise, int seasonNumber)
        {
            var season = franchise?.FindSeason(seasonNumber);
            if (franchise == null || season == null)
            {
                return null;
            }

            // A split season only owns its entry when it is the series' first: season 1 of Hunter x Hunter is where its one
            // 148-episode entry starts, while seasons 2 and 3 are cut out of the middle of it.
            var firstSeasonNumber = franchise.Seasons.Where(s => s.Number >= 1).Select(s => s.Number).DefaultIfEmpty(1).Min();
            var entry = season.TvEntry;

            return new SeasonEntry
            {
                AniListId = entry?.AniListId,
                MalId = entry?.MalId,
                KitsuId = entry?.KitsuId,
                Inferred = season.Inferred,
                OwnsEntry = entry != null && (!season.Inferred || seasonNumber == firstSeasonNumber),
                FromApi = true
            };
        }

        /// <summary>
        /// The entry for a season of the series whose AniList id is given. Season 0 (specials) has no single entry of its
        /// own and is not resolved here. Null when neither the API nor AniList's chain has the season.
        /// </summary>
        public async Task<SeasonEntry?> ResolveAsync(string? apiUrl, long seriesAniListId, int seasonNumber, CancellationToken cancellationToken)
        {
            if (seasonNumber < 1 || seriesAniListId <= 0)
            {
                return null;
            }

            var key = (seriesAniListId, seasonNumber);
            if (Cache.TryGetValue(key, out var cached)
                && DateTimeOffset.UtcNow - cached.At < (cached.Entry != null ? CacheDuration : MissCacheDuration))
            {
                return cached.Entry;
            }

            SeasonEntry? entry = null;
            if (!string.IsNullOrWhiteSpace(apiUrl))
            {
                var franchise = await _apiClient.GetFranchiseAsync(apiUrl, seriesAniListId, cancellationToken).ConfigureAwait(false);
                entry = FromFranchise(franchise, seasonNumber);
            }

            if (entry == null && seriesAniListId <= int.MaxValue)
            {
                var detail = await _apiService.GetSeasonByNumberAsync((int)seriesAniListId, seasonNumber).ConfigureAwait(false);
                if (detail != null)
                {
                    entry = new SeasonEntry { AniListId = detail.AniListId, MalId = detail.MalId, OwnsEntry = true };
                }
            }

            // An answer from AniList's chain while the API was supposed to answer (it was down) is only a stand-in: keep it briefly.
            var standIn = entry != null && !entry.FromApi && !string.IsNullOrWhiteSpace(apiUrl);
            Cache[key] = (standIn ? DateTimeOffset.UtcNow - (CacheDuration - MissCacheDuration) : DateTimeOffset.UtcNow, entry);
            return entry;
        }
    }
}
