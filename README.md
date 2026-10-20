# Anime Multi Source (Jellyfin plugin)

Remote anime metadata, tags, artwork and people for Jellyfin using multiple sources (AniList, AniDB, Jikan/MAL, TVDB, Fanart.tv, Fribb mappings and .plexmatch files). Built for large libraries with cautious rate limiting and persistent caching.

## About this fork
I'm JohnnyTaxs. This is my fork of [webbster64/jellyfin-plugin-AnimeMultiSource](https://github.com/webbster64/jellyfin-plugin-AnimeMultiSource), maintained for Jellyfin 12 / net10.0. I don't maintain the upstream `jellyfin-10.11` branch. I keep upstream's core provider behavior where it makes sense; the list below covers only what differs from the original project's README and behavior, not the features we share.

### Differences from upstream
- **Episode stills and TMDB**: episode artwork falls back from TVDB to Jikan/MAL, Kitsu, and TMDB, with a standalone Jikan episode-image provider. TMDB accepts either a v3 API key or a v4 Read Access Token (`eyJ...`, sent as a Bearer header), and `.plexmatch` accepts `tmdbid`.
- **Manual Identify**: a series can be identified without a `.plexmatch` when Jellyfin stores the selected provider ID. When a `.plexmatch` is present, explicit `anilistid`/`malid`/`tmdbid` hints take precedence over conflicting hints.
- **Manual search fallbacks**: when AniList search has no result, fall back to Jikan/MAL and Kitsu.
- **Season-specific resolution**: season metadata and episode stills use the selected season's own MAL/Kitsu IDs rather than the season-1 root, including specials and later seasons.
- **Cache behavior**: AniList/AniDB entries live for 30 days instead of 5. AniList media/season/root caches are capped at 5,000 entries each, people at 20,000, and AniDB at 5,000; the same caps apply when restoring the disk cache.
- **Resilience**: Jikan gets a 15-second request timeout and a 10-minute pause after repeated failures; AniDB requests are serialized; season walks are bounded; retry-after values are clamped; image hosts are allowlisted; and cancellation flows through AniDB/AniList calls.
- **Safer Fribb loading**: mappings are parsed one record at a time, and a truncated download is rejected instead of replacing a good mapping set.
- **AnimeSchedule refresh gating**: the post-refresh queue and scheduled task cannot process the same series at the same time.
- **Release checks**: tag/version/ABI/framework consistency, package contents, checksums, and immutable tags are validated before publishing.

## Features
- Multi-source IDs: resolves AniList, AniDB, MAL, TVDB, TMDB, IMDb, Kitsu, AniSearch via Fribb mappings + optional `.plexmatch` overrides. Backfills gaps in Fribb's data from `manami-project/anime-offline-database` and (optionally) AnimeSchedule.net, for titles Fribb hasn't fully cross-referenced yet.
- Metadata: titles, descriptions, genres, studios, scores, relations/seasons, people (voice actors/staff), tags (AniDB).
- Images: fanart.tv + TVDB backdrops, posters, logos, season art with quality filters and limits. Episode stills fall back from TVDB to Jikan/MAL, Kitsu, and optionally TMDB.
- Excluded folders: skip metadata, artwork, upcoming episodes, and automatic tag refreshes for folders you do not want the plugin to touch (for example, an edited or re-cut series that does not match upstream databases).
- Original title: choose the original-language title, English title, romaji, or Japanese title independently of the display title.
- Upcoming episodes: optional AnimeSchedule.net integration populates Jellyfin's built-in "Upcoming" tab with real sub/dub-aware air dates. See [AnimeSchedule.net (Upcoming episodes)](#animeschedulenet-upcoming-episodes) below.
- Localized libraries: season folders named in other languages (e.g. French "Saison 1") resolve correctly; numbered season titles and TVDB episode translations follow Jellyfin's configured display language.
- Rate limits & backoff: AniList (30/min), Jikan (spacing, retry-after, 15 s request timeout, and a 10-minute pause after repeated failures), AniDB (soft cap with backoff and ban detection).
- Persistent caches: AniDB and AniList responses cached up to 30 days and persisted to disk to survive restarts. AniList media/season/root caches are capped at 5,000 entries each, people at 20,000, and AniDB at 5,000.

## Requirements
- Jellyfin 12.0.0+ (net10.0 plugin, ABI 12.0.0.0, built against the Jellyfin 12.0.0 packages for 12.x compatibility)
- API keys (all optional but recommended):
  - Fanart.tv personal API key (logos/backdrops)
  - AnimeSchedule.net API key (Upcoming episodes; get one from your AnimeSchedule.net account settings, API tab)

### .plexmatch files (strongly recommended)
- A `.plexmatch` file beside each series folder gives the most reliable metadata and image-provider ID resolution. Manual **Identify** can also work without one when Jellyfin stores the selected provider id on the series. Supported fields: `title`, `year`, `tvdbid`, `imdbid`, `tmdbid`, and `anilistid`/`malid`.
- `anilistid`/`malid` let a series resolve directly off AniList/MAL even when Fribb hasn't cross-referenced it to a TVDB/IMDb id yet (common for brand-new simulcasts) - add one of these instead of `tvdbid`/`imdbid` for those titles.
- Sonarr can generate `.plexmatch` automatically: go to **Settings → Metadata**, enable **Plex**, and tick the option to write `.plexmatch` files.
- Keep each `.plexmatch` file alongside its series folder—no further setup is needed.

## Installation
**Option 1: Plugin repository (recommended)**
1) Jellyfin Dashboard → Plugins → Repositories.
2) Add repository: Name `AnimeMultiSource`, URL `https://raw.githubusercontent.com/JohnnyTaxs/jellyfin-plugin-AnimeMultiSource/main/manifest.json`.
3) Go to Catalog, find **Anime Multi Source**, click Install.
4) Restart Jellyfin (then hard refresh browser: Ctrl+Shift+R / Cmd+Shift+R).

**Option 2: Manual install from release**
1) Download the latest `AnimeMultiSource_v*.zip` from the [releases](https://github.com/JohnnyTaxs/jellyfin-plugin-AnimeMultiSource/releases).
2) Extract into your Jellyfin `plugins/AnimeMultiSource/` folder.
3) Restart Jellyfin.

**Option 3: Build from source**
1) `dotnet build` (or `dotnet publish -c Release`) in the repo root.
2) Copy the contents of `Jellyfin.Plugin.AnimeMultiSource/bin/<Configuration>/net10.0/` into your Jellyfin `plugins/AnimeMultiSource/` folder.
3) Restart Jellyfin.

## Configuration
Open **Dashboard -> Plugins -> Anime Multi Source**:
- Enter Fanart.tv personal key (for logos/backdrops).
- Optionally enter a TMDB API key to enable episode-still fallback after TVDB, Jikan/MAL, and Kitsu. Either a v3 API key or a v4 Read Access Token (starts with `eyJ`) works; the plugin sends v4 tokens as a Bearer header.
- Choose the **Title Field** (romaji, English, or Japanese) and the **Original Title Field** independently; the original title can also be English.
- AniDB client name/version are internal identifiers used by the plugin's legacy AniDB HTTP API integration. They default to `mediabrowser` / `1`; there is no separate AniDB client to install or configure.
- Configure backdrop limits/quality and enable/disable sources as desired.
- Under **Exclusions**, enter one full server folder path per line (for example, `/data/media/anime/One Pace`; use container paths for Docker). The plugin skips metadata, artwork, upcoming episodes, and automatic tag refreshes for each folder and all its children. Save to apply; remove the path to resume updates. Existing metadata, images, and upcoming entries are kept. Exclusions apply only to this plugin; Jellyfin can still scan the files and run other providers.
- Approved genres: prefilled with a curated list; edit or clear as needed (one genre per line).

### Metadata and image providers
- In your anime library settings, leave only **Anime Multi Source** enabled under both **Metadata downloaders** and **Image fetchers**. It replaces the other anime metadata and artwork providers, including TVDB and Fanart.
- Disable separate AniDB and AniList plugins to prevent them from adding duplicate provider IDs to your items.
- **Missing Episode Fetcher** can remain enabled. If it causes unexpected results, disable it and report the problem.

### AnimeSchedule.net (Upcoming episodes)
Optional. Adds mapped anime to Jellyfin's built-in **Upcoming** tab (`Shows → Upcoming`) with real air dates, and backstops title/overview/id resolution when Fribb/Jikan/AniList come up short for a title.

1) Get an API key from your AnimeSchedule.net account settings (API tab).
2) Enter it under **Dashboard → Plugins → Anime Multi Source → AnimeSchedule.net**, and choose a schedule: **Sub**, **Dub**, or **Combined** (prefers dub, falls back to sub per-episode if no dub exists).
3) Leave the key blank to disable the feature entirely - nothing else changes.

Upcoming episodes refresh whenever a series is refreshed, and on a scheduled task (**Dashboard → Scheduled Tasks → Anime Multi Source → Refresh AnimeSchedule Upcoming Episodes**, every 8 hours by default) so schedule delays/reschedules that happen between refreshes still get picked up. Episode titles show as `TBA` - AnimeSchedule is a scheduling calendar, not an episode database, so no title is ever fabricated.

### Refresh Recently-Aired Anime Tags
A newly-aired anime's AniDB/MAL tags and genres are often thin at first - community tagging takes time to catch up. This scheduled task (**Dashboard → Scheduled Tasks → Anime Multi Source → Refresh Recently-Aired Anime Tags**, runs daily) automatically re-checks a series' full metadata about a week, then again about 3 months, after its last episode was added, so tags improve on their own without a manual refresh.

## Rate limits & caching
- AniList: spaced to ~30 req/min; cached 30 days; bounded at 5,000 media/season/root entries and 20,000 people entries; persisted on disk.
- AniDB: soft daily cap with slow mode; ban/limit responses trigger backoff; cached 30 days and capped at 5,000 entries; persisted on disk.
- Jikan/MAL: spaced (~2.5s) with retry-after; lightweight caching via AniList reuse where possible.
- TMDB: conservatively spaced to 4 requests/second and retries HTTP 429 responses. TMDB's free API access has no fixed published quota, but dynamic limits around 40 requests/second may apply and can change.
- Persistent cache file: `provider-cache.json` under the plugin data folder (fallback to `AppContext.BaseDirectory/AnimeMultiSourceCache`). Entries older than 30 days are discarded automatically, and the cache is capped at the limits above.

## Usage notes
- `.plexmatch` files are honored for ID hints (title/year/TVDB/IMDb/TMDB/AniList/MAL).
- If AniDB is temporarily paused, tags will be skipped for that window but metadata will still complete; caching prevents repeat hits after the first successful fetch.
- Logs include rate-limit waits, cache hits, and any AniDB backoff reasons to help diagnose slowdowns.

### File and folder naming
- Use standard season/episode naming: `Show Name/Season 1/Show Name - S01E12 - Title.mkv`. Avoid date/daily formats (e.g., `Show Name - 2022-03-23.mkv`) because Jellyfin/TVDB episode mapping will be skipped.
- Keep one series per folder with season subfolders (`Season 1`, `Season 2`, …). Place `.plexmatch` in the series root.
- If using Sonarr/Radarr, set the series type to “Standard” (not “Daily/Date”) so files are named with `SxxEyy`.

## Troubleshooting
- No tags? Check logs for AniDB backoff messages; waits clear automatically. After a successful tag pull, results are cached/persisted for 30 days.
- Jikan/MAL data missing for a few minutes? Logs will show `skipping Jikan until ...`; this is the automatic 10-minute pause after repeated Jikan failures. AniList data is used meanwhile and Jikan resumes on its own.
- Fanart/TVDB images missing? Verify keys and item has a TVDB ID. Episode stills also try Jikan/MAL and Kitsu when TVDB has none; add a TMDB key for the final fallback.
- Slow first scan on huge libraries is expected; subsequent scans benefit from caches.

## Releases
Releases are tag-driven. To publish one:
1. Set `Version`, `AssemblyVersion`, and `FileVersion` in `Directory.Build.props`, plus the version in `build.yaml` and `Jellyfin.Plugin.AnimeMultiSource/plugin.json` (they must all match).
2. Add the release notes to `CHANGELOG.md`.
3. Commit, then push an annotated tag such as `v1.1.0.4`.

The **Release** workflow builds and validates the package, creates the GitHub release, and prepends the new version to `manifest.json` on `main` while keeping older versions. No manual manifest edits or checksums are needed. See [RELEASING.md](RELEASING.md) for the full checklist.

## License
GPLv3 (matches Jellyfin plugin requirements).
