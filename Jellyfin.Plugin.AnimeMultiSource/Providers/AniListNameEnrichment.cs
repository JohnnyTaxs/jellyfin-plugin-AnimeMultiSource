using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.AnimeMultiSource.Providers
{
    /// <summary>
    /// The anime-lists API supplies the candidates (and so every id), but its titles can be missing (no English title) or
    /// its synonym list polluted by a sibling entry. For the few closest candidates AniList's own record, fetched by the
    /// id the API gave, replaces the names so the matcher compares clean ones. No id ever comes from AniList here.
    /// </summary>
    public static class AniListNameEnrichment
    {
        public static async Task EnrichAsync(ApiService apiService, IEnumerable<AnimeCandidate> candidates)
        {
            foreach (var candidate in candidates.Where(c => c.AniListId.HasValue && !c.SynonymsTrusted))
            {
                var media = await apiService.GetAniListMovieMediaAsync(candidate.AniListId!.Value).ConfigureAwait(false);
                if (media == null)
                {
                    continue;
                }

                candidate.English = media.Title?.English ?? candidate.English;
                candidate.Romaji = media.Title?.Romaji ?? candidate.Romaji;
                candidate.Native = media.Title?.Native ?? candidate.Native;
                candidate.Duration = media.Duration ?? candidate.Duration;
                candidate.Type = media.Format ?? candidate.Type;
                candidate.Year ??= media.StartDate?.Year;
                candidate.Synonyms.Clear();
                candidate.Synonyms.AddRange(media.Synonyms ?? new List<string>());
                candidate.SynonymsTrusted = true;
            }
        }
    }
}
