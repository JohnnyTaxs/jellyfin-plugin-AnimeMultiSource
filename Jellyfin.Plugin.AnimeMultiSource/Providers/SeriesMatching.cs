using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.AnimeMultiSource.Providers
{
    /// <summary>
    /// Chooses the anime series a folder name refers to when there is no .plexmatch file. Tested on 100 popular series
    /// and 9 real library folders (see notes 7c): it prefers refusing over guessing, and a franchise whose seasons are
    /// separate entries is resolved to its first season by the caller (via the AniList PREQUEL chain).
    /// </summary>
    public static class SeriesMatcher
    {
        // Entries named after a later season ("2nd Season", "Season 3", "Part 2", "II", "Final Season") are never the root
        // of a franchise. Not applied when the folder name itself contains such a marker.
        private static readonly Regex LaterSeason = new(
            @"\b(2nd|3rd|[4-9]th|second|third|fourth|fifth|final)\s+(season|stage|series|cour)\b"
            + @"|\bseason\s*(\d+|ii|iii|iv|v|vi)\b"
            + @"|\bpart\s*\d+\b"
            + @"|\s(ii|iii|iv|v|vi|vii|viii)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex ExtraWords = new(
            @"\b(recap|digest|summary|preview|making of|pv|trailer|prologue|manner movie|special edition|ovas?)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly HashSet<string> ShortFormats = new(StringComparer.OrdinalIgnoreCase)
        {
            "MUSIC", "CM", "PV"
        };

        public static MatchOptions Options { get; } = new MatchOptions { MinLead = 0.08 };

        public static bool LooksLikeLaterSeason(string? title) =>
            !string.IsNullOrWhiteSpace(title) && LaterSeason.IsMatch(title.Trim());

        public static IReadOnlyList<ScoredCandidate> Rank(string query, int? year, IEnumerable<AnimeCandidate> candidates)
        {
            var queryHasSeasonMarker = LooksLikeLaterSeason(query);
            var queryHasExtraWord = ExtraWords.IsMatch(query);
            var scored = new List<ScoredCandidate>();

            foreach (var candidate in candidates)
            {
                if (!candidate.HasUsableId)
                {
                    continue;
                }

                var primary = new[] { candidate.English, candidate.Romaji, candidate.Native, candidate.Title };
                if (!queryHasSeasonMarker && primary.Any(LooksLikeLaterSeason))
                {
                    continue;
                }

                // Folder names often carry both names ("Food Wars! Shokugeki no Soma"); one entry has them in two fields.
                var bothNames = !string.IsNullOrWhiteSpace(candidate.English) && !string.IsNullOrWhiteSpace(candidate.Romaji)
                    ? candidate.English + " " + candidate.Romaji
                    : null;
                var primaryScore = TitleMatcher.Score(query, primary.Append(bothNames), out var primaryName);
                var synonymScore = TitleMatcher.Score(query, candidate.Synonyms, out var synonymName)
                    * (candidate.SynonymsTrusted ? 1.0 : Options.UntrustedSynonymWeight);

                var nameScore = primaryScore >= synonymScore ? primaryScore : synonymScore;
                var nameUsed = primaryScore >= synonymScore ? primaryName : synonymName;
                var score = nameScore;

                if (year.HasValue)
                {
                    var yearsOff = candidate.Year.HasValue ? Math.Abs(candidate.Year.Value - year.Value) : 1;
                    score += yearsOff == 0 ? 0.05 : -0.05 * yearsOff;
                }

                if (string.Equals(candidate.Type, "TV", StringComparison.OrdinalIgnoreCase))
                {
                    score += 0.10;
                }
                else if (string.Equals(candidate.Type, "MOVIE", StringComparison.OrdinalIgnoreCase))
                {
                    score -= 0.15;
                }
                else if (candidate.Type != null && ShortFormats.Contains(candidate.Type))
                {
                    score -= 0.30;
                }

                if (!queryHasExtraWord)
                {
                    var allNames = string.Join(" | ", primary.Where(n => !string.IsNullOrWhiteSpace(n)).Concat(candidate.Synonyms));
                    if (ExtraWords.IsMatch(allNames))
                    {
                        score -= 0.25;
                    }
                }

                scored.Add(new ScoredCandidate(candidate, score, nameScore, nameUsed));
            }

            return scored.OrderByDescending(s => s.Score).ToList();
        }

        public static MatchDecision Choose(string query, int? year, IEnumerable<AnimeCandidate> candidates) =>
            Decide(Rank(query, year, candidates));

        /// <summary>
        /// The usual rule (good score, clear lead), plus: an entry whose title equals the folder name exactly, when no other
        /// entry does, wins even if a spin-off or special scores close behind it (Slime vs "Slime Diaries", an "OVAs" entry).
        /// Two exact matches (the 2006 and 2009 "Haruhi") stay ambiguous.
        /// </summary>
        public static MatchDecision Decide(IReadOnlyList<ScoredCandidate> ranked)
        {
            var decision = MovieMatcher.Decide(ranked, Options);
            if (decision.Accepted || ranked.Count < 2)
            {
                return decision;
            }

            var best = ranked[0];
            if (best.NameScore >= 0.999 && best.Score >= Options.AcceptScore && ranked.Skip(1).All(r => r.NameScore < 0.999))
            {
                decision.Accepted = true;
                decision.Reason = $"the only exact title match (score {best.Score:F2}, lead {decision.Lead:F2})";
            }

            return decision;
        }

        /// <summary>
        /// The candidates that are too close to the best one to tell apart, but each a good title match. A franchise
        /// listed as several entries lands here; the caller accepts it when they all lead back to the same first season.
        /// </summary>
        public static IReadOnlyList<ScoredCandidate> TiedLeaders(IReadOnlyList<ScoredCandidate> ranked)
        {
            if (ranked.Count < 2)
            {
                return Array.Empty<ScoredCandidate>();
            }

            var best = ranked[0].Score;
            return ranked
                .Where(r => r.Score >= Options.AcceptScore && best - r.Score < Options.MinLead)
                .ToList();
        }
    }
}
