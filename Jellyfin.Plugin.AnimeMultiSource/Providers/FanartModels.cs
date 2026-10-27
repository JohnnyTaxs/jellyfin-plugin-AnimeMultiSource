using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.AnimeMultiSource.Providers
{
    public class FanartTvResponse
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("thetvdb_id")]
        public string? TvdbId { get; set; }

        [JsonPropertyName("tvposter")]
        public List<FanartImage> TvPosters { get; set; } = new();

        [JsonPropertyName("clearart")]
        public List<FanartImage> ClearArt { get; set; } = new();

        [JsonPropertyName("showbackground")]
        public List<FanartImage> ShowBackgrounds { get; set; } = new();

        [JsonPropertyName("tvbanner")]
        public List<FanartImage> TvBanners { get; set; } = new();

        [JsonPropertyName("hdtvlogo")]
        public List<FanartImage> HdTvLogos { get; set; } = new();

        [JsonPropertyName("clearlogo")]
        public List<FanartImage> ClearLogos { get; set; } = new();

        [JsonPropertyName("tvthumb")]
        public List<FanartImage> TvThumbs { get; set; } = new();

        [JsonPropertyName("seasonposter")]
        public List<FanartSeasonImage> SeasonPosters { get; set; } = new();

        [JsonPropertyName("seasonbanner")]
        public List<FanartSeasonImage> SeasonBanners { get; set; } = new();

        [JsonPropertyName("seasonthumb")]
        public List<FanartSeasonImage> SeasonThumbs { get; set; } = new();
    }

    public class FanartMovieResponse
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("tmdb_id")]
        public string? TmdbId { get; set; }

        [JsonPropertyName("imdb_id")]
        public string? ImdbId { get; set; }

        [JsonPropertyName("movieposter")]
        public List<FanartImage> MoviePosters { get; set; } = new();

        [JsonPropertyName("moviebackground")]
        public List<FanartImage> MovieBackgrounds { get; set; } = new();

        [JsonPropertyName("hdmovielogo")]
        public List<FanartImage> HdMovieLogos { get; set; } = new();

        [JsonPropertyName("movielogo")]
        public List<FanartImage> MovieLogos { get; set; } = new();

        [JsonPropertyName("hdmovieclearart")]
        public List<FanartImage> HdMovieClearArt { get; set; } = new();

        [JsonPropertyName("movieart")]
        public List<FanartImage> MovieArt { get; set; } = new();

        [JsonPropertyName("moviebanner")]
        public List<FanartImage> MovieBanners { get; set; } = new();

        [JsonPropertyName("moviethumb")]
        public List<FanartImage> MovieThumbs { get; set; } = new();

        [JsonPropertyName("moviedisc")]
        public List<FanartImage> MovieDiscs { get; set; } = new();
    }

    public class FanartImage
    {
        [JsonPropertyName("url")]
        public string? Url { get; set; }

        [JsonPropertyName("lang")]
        public string? Language { get; set; }
    }

    public class FanartSeasonImage : FanartImage
    {
        [JsonPropertyName("season")]
        public string? Season { get; set; }
    }
}
