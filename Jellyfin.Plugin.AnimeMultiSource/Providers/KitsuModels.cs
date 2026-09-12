using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.AnimeMultiSource.Providers
{
    public class KitsuSearchResponse
    {
        [JsonPropertyName("data")]
        public List<KitsuAnime>? Data { get; set; }
    }

    public class KitsuAnime
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("attributes")]
        public KitsuAnimeAttributes? Attributes { get; set; }
    }

    public class KitsuAnimeAttributes
    {
        [JsonPropertyName("canonicalTitle")]
        public string? CanonicalTitle { get; set; }

        [JsonPropertyName("titles")]
        public Dictionary<string, string>? Titles { get; set; }

        [JsonPropertyName("startDate")]
        public string? StartDate { get; set; }

        [JsonPropertyName("posterImage")]
        public KitsuPosterImage? PosterImage { get; set; }
    }

    public class KitsuPosterImage
    {
        [JsonPropertyName("original")]
        public string? Original { get; set; }
    }

    public class KitsuEpisodeResponse
    {
        [JsonPropertyName("data")]
        public List<KitsuEpisode>? Data { get; set; }
    }

    public class KitsuEpisode
    {
        [JsonPropertyName("attributes")]
        public KitsuEpisodeAttributes? Attributes { get; set; }
    }

    public class KitsuEpisodeAttributes
    {
        [JsonPropertyName("seasonNumber")]
        public int? SeasonNumber { get; set; }

        [JsonPropertyName("number")]
        public int? Number { get; set; }

        [JsonPropertyName("thumbnail")]
        public KitsuPosterImage? Thumbnail { get; set; }
    }
}