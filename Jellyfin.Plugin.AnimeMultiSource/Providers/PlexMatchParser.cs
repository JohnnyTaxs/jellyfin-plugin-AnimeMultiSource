using System;
using System.IO;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AnimeMultiSource.Providers
{
    public class PlexMatchParser
    {
        private readonly ILogger _logger;

        public PlexMatchParser(ILogger logger)
        {
            _logger = logger;
        }

        public PlexMatchData ParsePlexMatch(string content)
        {
            var data = new PlexMatchData();

            foreach (var line in content.Split('\n'))
            {
                var trimmedLine = line.Trim();
                if (string.IsNullOrEmpty(trimmedLine)) continue;

                var parts = trimmedLine.Split(new[] { ':' }, 2);
                if (parts.Length != 2) continue;

                var key = parts[0].Trim();
                var value = parts[1].Trim();

                switch (key.ToLowerInvariant())
                {
                    case "title":
                    case "show":
                        data.Title = value;
                        _logger?.LogDebug("Parsed Title: {Title}", value);
                        break;
                    case "guid":
                        ApplyGuid(data, value);
                        break;
                    case "year":
                        if (int.TryParse(value, out int year))
                        {
                            data.Year = year;
                            _logger?.LogDebug("Parsed Year: {Year}", year);
                        }
                        break;
                    case "tvdbid":
                        data.TvdbId = value;
                        _logger?.LogDebug("Parsed TVDB ID: {TvdbId}", value);
                        break;
                    case "imdbid":
                        data.ImdbId = value;
                        _logger?.LogDebug("Parsed IMDb ID: {ImdbId}", value);
                        break;
                    case "tmdbid":
                    case "themoviedbid":
                        if (long.TryParse(value, out var tmdbId))
                        {
                            data.TmdbId = tmdbId;
                            _logger?.LogDebug("Parsed TMDB ID: {TmdbId}", tmdbId);
                        }
                        break;
                    case "anilistid":
                        if (long.TryParse(value, out var anilistId))
                        {
                            data.AniListId = anilistId;
                            _logger?.LogDebug("Parsed AniList ID: {AniListId}", anilistId);
                        }
                        break;
                    case "malid":
                        if (long.TryParse(value, out var malId))
                        {
                            data.MalId = malId;
                            _logger?.LogDebug("Parsed MAL ID: {MalId}", malId);
                        }
                        break;
                }
            }

            return data;
        }

        // Plex documents guid hints such as tvdb://12345 and the older
        // com.plexapp.agents.thetvdb://12345 form.
        private static void ApplyGuid(PlexMatchData data, string guid)
        {
            var separator = guid.IndexOf("://", StringComparison.Ordinal);
            if (separator <= 0)
            {
                return;
            }

            var source = guid[..separator].ToLowerInvariant();
            var id = guid[(separator + 3)..];
            var suffix = id.IndexOfAny(new[] { '?', '#' });
            if (suffix >= 0)
            {
                id = id[..suffix];
            }

            id = id.Trim('/');
            if (string.IsNullOrWhiteSpace(id))
            {
                return;
            }

            if (source.Contains("anilist", StringComparison.Ordinal))
            {
                if (long.TryParse(id, out var aniListId)) data.AniListId = aniListId;
            }
            else if (source.Contains("myanimelist", StringComparison.Ordinal) ||
                     source.EndsWith("mal", StringComparison.Ordinal))
            {
                if (long.TryParse(id, out var malId)) data.MalId = malId;
            }
            else if (source.Contains("themoviedb", StringComparison.Ordinal) ||
                     source.EndsWith("tmdb", StringComparison.Ordinal))
            {
                if (long.TryParse(id, out var tmdbId)) data.TmdbId = tmdbId;
            }
            else if (source.Contains("thetvdb", StringComparison.Ordinal) ||
                     source.EndsWith("tvdb", StringComparison.Ordinal))
            {
                data.TvdbId = id;
            }
            else if (source.Contains("imdb", StringComparison.Ordinal))
            {
                data.ImdbId = id;
            }
        }
    }

    public class PlexMatchData
    {
        public string Title { get; set; } = string.Empty;
        public int? Year { get; set; }
        public string TvdbId { get; set; } = string.Empty;
        public string ImdbId { get; set; } = string.Empty;
        public long? TmdbId { get; set; }
        public long? AniListId { get; set; }
        public long? MalId { get; set; }
    }
}