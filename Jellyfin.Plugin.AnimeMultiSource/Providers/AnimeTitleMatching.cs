using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.AnimeMultiSource.Providers
{
    /// <summary>
    /// Title normalisation and similarity. Used to decide which anime entry a folder name refers to
    /// when there is no .plexmatch file. Pure code (no I/O) so it can be unit tested.
    /// </summary>
    public static class TitleMatcher
    {
        private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
        {
            "the", "a", "an", "of", "and", "movie", "film", "gekijouban", "theatrical"
        };

        private static readonly Regex YearInParentheses = new(@"\(\d{4}\)", RegexOptions.Compiled);
        private static readonly Regex NotLetterOrDigit = new(@"[^\p{L}\p{N}]+", RegexOptions.Compiled);

        /// <summary>Lower-cased, accent-folded word list with punctuation, "(2019)" and stop words removed.</summary>
        public static IReadOnlyList<string> Tokens(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return Array.Empty<string>();
            }

            var folded = FoldDiacritics(text).ToLowerInvariant();
            folded = YearInParentheses.Replace(folded, " ");
            folded = NotLetterOrDigit.Replace(folded, " ");
            return folded
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Where(token => !StopWords.Contains(token))
                .ToArray();
        }

        /// <summary>Sorensen-Dice coefficient over the two token sets (0..1).</summary>
        public static double Dice(IReadOnlyCollection<string> a, IReadOnlyCollection<string> b)
        {
            if (a.Count == 0 || b.Count == 0)
            {
                return 0;
            }

            var setA = new HashSet<string>(a, StringComparer.Ordinal);
            var setB = new HashSet<string>(b, StringComparer.Ordinal);
            var shared = setA.Count(setB.Contains);
            return 2.0 * shared / (setA.Count + setB.Count);
        }

        /// <summary>Best similarity between <paramref name="query"/> and any of <paramref name="names"/>; an exact normalised match is 1.0.</summary>
        public static double Score(string query, IEnumerable<string?> names, out string? bestName)
        {
            var queryTokens = Tokens(query);
            var queryKey = string.Join(' ', queryTokens);
            var best = 0.0;
            bestName = null;

            foreach (var name in names)
            {
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                var tokens = Tokens(name);
                var score = queryKey.Length > 0 && string.Join(' ', tokens) == queryKey
                    ? 1.0
                    : Dice(queryTokens, tokens);
                if (score > best)
                {
                    best = score;
                    bestName = name;
                }
            }

            return best;
        }

        private static string FoldDiacritics(string text)
        {
            var decomposed = text.Normalize(NormalizationForm.FormD);
            var builder = new StringBuilder(decomposed.Length);
            foreach (var c in decomposed)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                {
                    builder.Append(c);
                }
            }

            return builder.ToString().Normalize(NormalizationForm.FormC);
        }
    }

    /// <summary>One possible anime entry for a folder name, from the anime-lists API or from AniList.</summary>
    public sealed class AnimeCandidate
    {
        /// <summary>"api" or "anilist".</summary>
        public string Source { get; set; } = string.Empty;

        public long? AniListId { get; set; }

        public long? MalId { get; set; }

        public long? AniDbId { get; set; }

        public long? KitsuId { get; set; }

        public long? AniSearchId { get; set; }

        public long? TmdbId { get; set; }

        public List<string> ImdbIds { get; } = new();

        /// <summary>The source's main title (usually romaji).</summary>
        public string? Title { get; set; }

        public string? English { get; set; }

        public string? Romaji { get; set; }

        public string? Native { get; set; }

        public List<string> Synonyms { get; } = new();

        /// <summary>
        /// True when the synonym list is known to be clean (AniList). The anime-lists API merges synonyms from several
        /// sources and one of them can attach a sibling film's titles, so API synonyms are weighted down.
        /// </summary>
        public bool SynonymsTrusted { get; set; }

        /// <summary>MOVIE, TV, OVA, ONA, SPECIAL, MUSIC ...</summary>
        public string? Type { get; set; }

        public int? Year { get; set; }

        /// <summary>Episode runtime in minutes.</summary>
        public int? Duration { get; set; }

        /// <summary>The API's own verdict: exact / partial.</summary>
        public string? ApiTitleMatch { get; set; }

        /// <summary>The API's own verdict: exact / near.</summary>
        public string? ApiYearMatch { get; set; }

        public bool HasUsableId => AniListId.HasValue || MalId.HasValue;

        public string Describe() =>
            $"{(AniListId.HasValue ? "AniList " + AniListId : "MAL " + MalId)} '{Title ?? Romaji ?? English}' {Type} {Year}";
    }

    public sealed class ScoredCandidate
    {
        public ScoredCandidate(AnimeCandidate candidate, double score, double nameScore, string? nameUsed)
        {
            Candidate = candidate;
            Score = score;
            NameScore = nameScore;
            NameUsed = nameUsed;
        }

        public AnimeCandidate Candidate { get; }

        public double Score { get; }

        public double NameScore { get; }

        public string? NameUsed { get; }
    }

    public sealed class MatchOptions
    {
        /// <summary>The leader must score at least this much...</summary>
        public double AcceptScore { get; set; } = 0.80;

        /// <summary>...and lead the runner-up by at least this much.</summary>
        public double MinLead { get; set; } = 0.15;

        /// <summary>A lone candidate the API itself matched (title exact/partial + year exact) only needs this score.</summary>
        public double LoneCandidateMinScore { get; set; } = 0.40;

        /// <summary>Weight of an untrusted synonym match relative to a main-title match.</summary>
        public double UntrustedSynonymWeight { get; set; } = 0.92;
    }

    public sealed class MatchDecision
    {
        public bool Accepted { get; set; }

        public ScoredCandidate? Best { get; set; }

        public double Lead { get; set; }

        public string Reason { get; set; } = string.Empty;

        public IReadOnlyList<ScoredCandidate> Ranked { get; set; } = Array.Empty<ScoredCandidate>();
    }

    /// <summary>
    /// Chooses the right anime movie from a list of candidates. Tuned on 130 real movie folders (128 correct, 0 wrong,
    /// the rest refused): it prefers refusing over guessing, so an unclear case is logged and left for a .plexmatch.
    /// </summary>
    public static class MovieMatcher
    {
        private static readonly Regex ExtraWords = new(
            @"\b(manner movie|recap|digest|summary|preview|making of|pv|trailer|prologue|special edition)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly HashSet<string> ShortFormats = new(StringComparer.OrdinalIgnoreCase)
        {
            "MUSIC", "CM", "PV", "TV_SHORT"
        };

        private const int ShortRuntimeMinutes = 15;

        public static MatchDecision Choose(string query, int? year, IEnumerable<AnimeCandidate> candidates, MatchOptions? options = null)
        {
            options ??= new MatchOptions();
            return Decide(Rank(query, year, candidates, options), options);
        }

        public static IReadOnlyList<ScoredCandidate> Rank(string query, int? year, IEnumerable<AnimeCandidate> candidates, MatchOptions? options = null)
        {
            options ??= new MatchOptions();
            var queryHasExtraWord = ExtraWords.IsMatch(query);
            var scored = new List<ScoredCandidate>();

            foreach (var candidate in candidates)
            {
                // An entry with neither an AniList nor a MAL id (only TMDb/IMDb) gives us nothing to fetch metadata with.
                if (!candidate.HasUsableId)
                {
                    continue;
                }

                var primary = new[] { candidate.English, candidate.Romaji, candidate.Native, candidate.Title };
                var primaryScore = TitleMatcher.Score(query, primary, out var primaryName);
                var synonymScore = TitleMatcher.Score(query, candidate.Synonyms, out var synonymName)
                    * (candidate.SynonymsTrusted ? 1.0 : options.UntrustedSynonymWeight);

                var nameScore = primaryScore >= synonymScore ? primaryScore : synonymScore;
                var nameUsed = primaryScore >= synonymScore ? primaryName : synonymName;

                var score = nameScore;

                if (year.HasValue)
                {
                    // Unknown candidate year counts as one year off.
                    var yearsOff = candidate.Year.HasValue ? Math.Abs(candidate.Year.Value - year.Value) : 1;
                    score += yearsOff == 0 ? 0.05 : -0.05 * yearsOff;
                }

                if (string.Equals(candidate.Type, "MOVIE", StringComparison.OrdinalIgnoreCase))
                {
                    score += 0.10;
                }

                // "Manner Movie", recap and preview entries share the film's title but are not the film.
                if (!queryHasExtraWord)
                {
                    var allNames = string.Join(" | ", primary.Where(n => !string.IsNullOrWhiteSpace(n)).Concat(candidate.Synonyms));
                    if (ExtraWords.IsMatch(allNames))
                    {
                        score -= 0.25;
                    }
                }

                // A folder named like a feature film is not a one-to-three minute clip or music video.
                var isShort = (candidate.Type != null && ShortFormats.Contains(candidate.Type))
                    || (candidate.Duration.HasValue && candidate.Duration.Value < ShortRuntimeMinutes);
                if (isShort)
                {
                    score -= 0.30;
                }

                scored.Add(new ScoredCandidate(candidate, score, nameScore, nameUsed));
            }

            return scored.OrderByDescending(s => s.Score).ToList();
        }

        public static MatchDecision Decide(IReadOnlyList<ScoredCandidate> ranked, MatchOptions? options = null)
        {
            options ??= new MatchOptions();
            var decision = new MatchDecision { Ranked = ranked };

            if (ranked.Count == 0)
            {
                decision.Reason = "no usable candidates";
                return decision;
            }

            var best = ranked[0];
            decision.Best = best;
            decision.Lead = ranked.Count > 1 ? best.Score - ranked[1].Score : 1.0;

            if (best.Score >= options.AcceptScore && decision.Lead >= options.MinLead)
            {
                decision.Accepted = true;
                decision.Reason = ranked.Count > 1
                    ? $"clear leader (score {best.Score:F2}, lead {decision.Lead:F2})"
                    : $"only candidate (score {best.Score:F2})";
                return decision;
            }

            // The title may just be spelled differently ("Coexistence" vs "Coexistance"); a lone candidate that the API
            // itself matched on title and year is still trustworthy.
            if (ranked.Count == 1
                && best.Candidate.ApiTitleMatch is "exact" or "partial"
                && best.Candidate.ApiYearMatch == "exact"
                && best.Score >= options.LoneCandidateMinScore)
            {
                decision.Accepted = true;
                decision.Reason = $"lone candidate matched by the API (score {best.Score:F2})";
                return decision;
            }

            decision.Reason = ranked.Count > 1
                ? $"no clear leader (score {best.Score:F2}, lead {decision.Lead:F2})"
                : $"weak match (score {best.Score:F2})";
            return decision;
        }
    }
}
