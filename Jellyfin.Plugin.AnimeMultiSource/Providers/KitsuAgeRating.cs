using System;

namespace Jellyfin.Plugin.AnimeMultiSource.Providers
{
    /// <summary>
    /// Turns Kitsu's age rating (<c>ageRating</c> G/PG/R/R18 plus the free-text <c>ageRatingGuide</c>) into the wording
    /// Jikan/MAL uses ("PG-13 - Teens 13 or older"), so a rating taken from Kitsu looks the same as one taken from Jikan.
    /// Only a fallback for when Jikan is unreachable or has no rating for the title.
    /// </summary>
    public static class KitsuAgeRating
    {
        public static string? ToJikanStyle(string? ageRating, string? guide)
        {
            var rating = (ageRating ?? string.Empty).Trim().ToUpperInvariant();
            var text = (guide ?? string.Empty).Trim();

            switch (rating)
            {
                case "G":
                    return "G - All Ages";

                case "PG":
                    if (Contains(text, "children") && !Contains(text, "teens"))
                    {
                        return "PG - Children";
                    }

                    // Kitsu files MAL's PG-13 under PG with the guide "Teens 13 or older"; a bare PG is mostly the same
                    // (specials and later seasons of PG-13 shows), so take the stricter reading.
                    return "PG-13 - Teens 13 or older";

                case "R":
                    // MAL's R+ is Kitsu's R with a nudity note ("Mild Nudity", "Violence, Nudity").
                    return Contains(text, "nudity")
                        ? "R+ - Mild Nudity"
                        : "R - 17+ (violence & profanity)";

                case "R18":
                    return "Rx - Hentai";

                default:
                    return null;
            }
        }

        private static bool Contains(string text, string value) =>
            text.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
