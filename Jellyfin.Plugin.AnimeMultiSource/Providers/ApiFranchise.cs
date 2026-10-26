using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.AnimeMultiSource.Providers
{
    /// <summary>
    /// A series as TVDB/TMDb see it (Dragon Ball Z: one series, nine "saga" seasons), with the anime entries behind each
    /// season, from the anime-lists API. AniList and MAL often have one long entry or a sequel chain that crosses into a
    /// different series (Dragon Ball Z is a sequel of Dragon Ball, Dragon Ball Super a sequel of Dragon Ball Kai), so the
    /// season layout and the first season are taken from here instead of from AniList's relations.
    /// </summary>
    public sealed class ApiFranchise
    {
        public string? Title { get; set; }

        public int? Year { get; set; }

        public long? TvdbId { get; set; }

        public long? TmdbId { get; set; }

        public int? MatchedSeason { get; set; }

        public List<ApiSeason> Seasons { get; } = new();

        public ApiSeason? FindSeason(int number) => Seasons.FirstOrDefault(s => s.Number == number);

        /// <summary>
        /// The AniList id of the franchise's first real season (lowest season number above 0): the "root" the rest of the
        /// plugin means. Null when the API has no season with a usable TV-like entry.
        /// </summary>
        public long? FirstSeasonAniListId => FirstSeasonEntry?.AniListId;

        /// <summary>The anime entry of the latest real season: the one whose last episode ends the show.</summary>
        public AnimeCandidate? LatestSeasonEntry =>
            Seasons.Where(s => s.Number >= 1).OrderByDescending(s => s.Number)
                .Select(s => s.TvEntry)
                .FirstOrDefault(entry => entry != null);

        /// <summary>The anime entry of the first real season (see <see cref="FirstSeasonAniListId"/>), with all its ids.</summary>
        public AnimeCandidate? FirstSeasonEntry =>
            Seasons.Where(s => s.Number >= 1).OrderBy(s => s.Number)
                .Select(s => s.TvEntry)
                .FirstOrDefault(entry => entry != null);
    }

    public sealed class ApiSeason
    {
        public int Number { get; set; }

        public int? Episodes { get; set; }

        /// <summary>True when one long entry was split to fit this season (the entry covers several seasons).</summary>
        public bool Inferred { get; set; }

        public List<AnimeCandidate> Entries { get; } = new();

        /// <summary>The entry that is the season's own show (TV/ONA/TV_SHORT), not a film or special mapped into it.</summary>
        public AnimeCandidate? TvEntry =>
            Entries.FirstOrDefault(e => e.AniListId.HasValue && IsSeriesFormat(e.Type));

        private static bool IsSeriesFormat(string? type) =>
            type != null && (type.Equals("TV", StringComparison.OrdinalIgnoreCase)
                || type.Equals("ONA", StringComparison.OrdinalIgnoreCase)
                || type.Equals("TV_SHORT", StringComparison.OrdinalIgnoreCase));
    }
}
