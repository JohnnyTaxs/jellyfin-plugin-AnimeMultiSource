# AGENTS.md

## What this repository is

A Jellyfin 12 plugin that supplies anime metadata, tags, artwork, people, episode stills, and upcoming episodes from AniList, AniDB, Jikan/MAL, TVDB, Kitsu, TMDB, Fanart.tv, Fribb mappings, and `.plexmatch` files.

This is JohnnyTaxs' fork of `webbster64/jellyfin-plugin-AnimeMultiSource`. It targets Jellyfin `12.0.0.0` / `net10.0` only; the upstream `jellyfin-10.11` branch is not maintained here. Keep upstream-compatible behavior unless a fork-specific fix requires otherwise.

## Stack and compatibility

- C# / .NET 10 (`net10.0`), nullable enabled.
- Jellyfin ABI `12.0.0.0`.
- Jellyfin assemblies are compile-time references only and must never be shipped in the plugin zip.
- `global.json` pins an SDK version with `rollForward: latestMinor`.
- There is no unit-test project. Verification is build, package validation, and GitHub Actions.

## Repository map

- `Jellyfin.Plugin.AnimeMultiSource/Plugin.cs`: plugin entry point, configuration, startup cleanup.
- `Jellyfin.Plugin.AnimeMultiSource/PluginServiceRegistrator.cs`: DI registration and hosted services.
- `Jellyfin.Plugin.AnimeMultiSource/Constants.cs`: provider IDs, URLs, cache names, plugin metadata.
- `Configuration/PluginConfiguration.cs`: persisted settings and defaults.
- `Configuration/configPage.html`: embedded configuration UI. Field IDs here must match the load/save code.
- `Providers/AnimeMultiSourceProvider.cs`: series metadata provider and manual search.
- `Providers/AnimeSeasonProvider.cs`: season metadata, season-number resolution, exclusions.
- `Providers/AnimeMultiSourceService.cs`: series metadata orchestration and title/overview fallbacks.
- `Providers/TvdbEpisodeProvider.cs`: episode metadata.
- `Providers/TvdbEpisodeImageProvider.cs`: episode stills: TVDB → Jikan/MAL → Kitsu → TMDB.
- `Providers/JikanEpisodeImageProvider.cs`: standalone Jikan episode-image provider.
- `Providers/ApiService.cs`: AniList/AniDB/Jikan/Kitsu access, rate limiting, caching, people, tagging.
- `Providers/AnimeListMapper.cs`: Fribb mapping load, ID lookup, offline backfill.
- `Providers/TvdbApiClient.cs`, `FanartClient.cs`: TVDB and Fanart.tv clients.
- `Providers/AnimeScheduleClient.cs`: AnimeSchedule.net API client.
- `Providers/AnimeScheduleMissingEpisodeProvider.cs`: queues series after refresh.
- `Providers/AnimeScheduleRefreshTask.cs`: scheduled whole-library refresh.
- `Providers/AnimeScheduleUpcomingEpisodeUpdater.cs`: shared refresh logic and per-series gate.
- `Providers/AnimeTagRefreshTask.cs`: rechecks recently aired series and trims provider caches.
- `.github/workflows/build.yml`: build and package validation on pushes/PRs.
- `.github/workflows/release.yml`: tag-driven release, archive, checksum, manifest update.
- `.github/scripts/validate-metadata.sh`: version/GUID/ABI/framework/manifest consistency.
- `build.yaml`, `plugin.json`, `Directory.Build.props`: version and catalog metadata.
- `manifest.json`: plugin catalog. Updated by the release workflow, not by hand during normal releases.

## Build and validate

From the repository root:

```bash
dotnet restore jellyfin-plugin-AnimeMultiSource.sln
dotnet build jellyfin-plugin-AnimeMultiSource.sln -c Release --no-restore
bash .github/scripts/validate-metadata.sh
```

To reproduce the release package locally:

```bash
dotnet publish Jellyfin.Plugin.AnimeMultiSource/Jellyfin.Plugin.AnimeMultiSource.csproj \
  -c Release -o publish
bash .github/scripts/create-meta-json.sh
(cd publish && zip -q -r ../AnimeMultiSource_v<version>.zip .)
```

The publish output must contain the plugin DLL, `plugin.json`, `meta.json`, deps, and XML docs, and must not contain `MediaBrowser.*.dll`, `Emby.*.dll`, or other Jellyfin server assemblies. `publish/` is gitignored.

Always finish code changes with a clean Release build: zero warnings and zero errors.

## Required invariants

### Versioning

Version lives in five places and must match exactly:

- `Directory.Build.props`: `Version`, `AssemblyVersion`, `FileVersion`
- `build.yaml`: `version`
- `Jellyfin.Plugin.AnimeMultiSource/plugin.json`: `version`

Use four-part versions: `MAJOR.MINOR.PATCH.BUILD` (for example `1.1.0.4`). Do not bump versions for docs-only changes. Do not re-tag or replace an existing release; bump the patch version instead.

### Release process

1. Add a `## <version> - <date>` section to `CHANGELOG.md`.
2. Update all five version values.
3. Run the Release build and package checks.
4. Commit and push `main`.
5. Create and push an annotated tag such as `v1.1.0.4`.
6. Confirm Build and Release workflows succeed, the release asset exists, and `manifest.json` was updated by the workflow.

Release notes are extracted from the matching `CHANGELOG.md` section. `build.yaml`'s `changelog` is catalog text, not the release-notes source. Do not manually edit `manifest.json` unless recovering a failed release.

### Provider behavior

- `.plexmatch` is authoritative when present. Explicit `anilistid`/`malid`/`tmdbid` hints override conflicting TVDB/IMDb/TMDB hints.
- Manual Identify without `.plexmatch` must keep working by resolving IDs Jellyfin stored on the series.
- Episode stills order is TVDB → Jikan/MAL → Kitsu → TMDB. Keep the standalone Jikan provider working.
- TMDB accepts either a v3 API key or a v4 Read Access Token. Tokens beginning with `eyJ` must use `Authorization: Bearer`; v3 keys use `api_key`.
- Season metadata must use the selected season's own IDs, not blindly reuse the season-1 root.
- Specials/Season 0 must not inherit one OVA's title, overview, or IDs for the whole season.
- Do not break localized season-folder parsing or exclusion handling for virtual seasons.

### Networking and caches

- All new external HTTP calls must accept and forward `CancellationToken` where the call site has one.
- Dispose `HttpResponseMessage` and request/content objects.
- Respect existing rate limits and backoff:
  - AniList: ~30 req/min.
  - AniDB: serialized requests, daily soft cap, slow mode, ban detection.
  - Jikan: spacing, retry-after handling, 15-second timeout, 10-minute pause after repeated failures.
  - TMDB: conservative spacing and HTTP 429 retries.
- Only return image URLs that pass `ImageUrlValidator`; do not add arbitrary remote image hosts.
- Preserve TVDB continuation-host validation and incomplete-list handling.
- AniList/AniDB caches are 30 days. Caps are:
  - AniList media/season/root: 5,000 each.
  - AniList people: 20,000.
  - AniDB: 5,000.
- Persistent cache is `provider-cache.json`; restore and store paths must enforce the same TTL and caps.
- Keep `ApiService.TrimCaches()` working from the scheduled tag-refresh task.

### Fribb mappings

- Parse records individually; one malformed row must not reject the whole download.
- Reject a download with fewer than 1,000 usable mappings, or less than half the previous successful count.
- Preserve the offline-database backfill behavior for missing AniDB/Kitsu/AniSearch/LiveChart IDs.
- Cache only complete, validated mappings.

### AnimeSchedule

- Do not block Jellyfin's refresh pipeline; queue work through `AnimeScheduleMissingEpisodeProvider`.
- Keep the per-series gate in `AnimeScheduleUpcomingEpisodeUpdater` so the queue and scheduled task cannot process the same series concurrently.
- Do not fabricate episode titles; AnimeSchedule entries use `TBA`.
- Keep upcoming refresh tolerant of schedule changes and cancellations.

## Coding conventions

- Match the existing style: four-space indentation, braces on new lines, nullable annotations, and the block-namespace style used throughout the project.
- Prefer small, targeted changes over refactors.
- Keep public provider IDs and configuration names stable for upgrade compatibility.
- When adding configuration, update all of:
  - `PluginConfiguration.cs` default
  - `configPage.html` field, load, and save paths
  - any validation or migration logic
- Use `Plugin.GetConfigurationSafe(...)` in request paths so saved settings apply without a restart.
- Do not log API keys, tokens, or full URLs containing secrets.
- Keep provider ordering intentional; anime metadata/image providers run before Jellyfin core providers.

## Security and secrets

- Never commit Fanart.tv keys, AnimeSchedule keys, TMDB keys/tokens, or other credentials.
- Jellyfin persists plugin configuration in its XML configuration, which is not encrypted. Do not describe it as secure secret storage.
- Redact or omit query strings when logging external URLs.
- Be careful with image and continuation URLs from external APIs; use the existing validation helpers instead of trusting them directly.

## Verification checklist

Before considering work complete:

- [ ] `dotnet build jellyfin-plugin-AnimeMultiSource.sln -c Release` passes with 0 warnings and 0 errors.
- [ ] `bash .github/scripts/validate-metadata.sh` passes.
- [ ] If packaging changed, inspect the zip for required files and no server assemblies.
- [ ] If settings changed, verify load/save and defaults in the config page.
- [ ] If provider behavior changed, check both `.plexmatch` and manual-Identify paths.
- [ ] If networking changed, check cancellation, timeout, rate limiting, and disposal.
- [ ] If release-related, confirm the tag matches all version fields and `CHANGELOG.md` has the matching section.
