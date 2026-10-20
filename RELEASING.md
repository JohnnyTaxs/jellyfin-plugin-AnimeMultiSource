# Releasing Anime Multi Source

Releases are created by GitHub Actions from an annotated tag. Do not build or upload the
zip by hand; the workflow does it and keeps `manifest.json` in sync.

## Checklist

1. Merge the changes to `main` and make sure CI is green.
2. Bump the version in **all three** places (they must match):
   - `Directory.Build.props`
   - `build.yaml`
   - `Jellyfin.Plugin.AnimeMultiSource/plugin.json`
3. Add a `## <version> - <date>` section to `CHANGELOG.md`.
4. Build and smoke-test locally:

   ```bash
   dotnet build jellyfin-plugin-AnimeMultiSource.sln -c Release
   dotnet publish Jellyfin.Plugin.AnimeMultiSource/Jellyfin.Plugin.AnimeMultiSource.csproj -c Release -o publish
   ```

   `publish/` must contain `Jellyfin.Plugin.AnimeMultiSource.dll` and `plugin.json`, and must
   **not** contain `MediaBrowser.*.dll`, `Emby.*.dll`, or Jellyfin server assemblies.
5. Commit the version bump and changelog.
6. Tag and push:

   ```bash
   git tag v1.1.0.1
   git push origin main
   git push origin v1.1.0.1
   ```

The **Release** workflow then:

- verifies the tag matches the three version files;
- builds, publishes, and validates the package;
- creates the GitHub release and uploads `AnimeMultiSource_v<version>.zip`;
- computes the MD5 checksum and prepends the release to `manifest.json` on `main`, keeping
  older catalog entries;
- commits the manifest update back to `main`.

## After the workflow

- Confirm the release exists and the manifest on `main` lists the new version and checksum.
- In Jellyfin, refresh the catalog and install/upgrade. A server restart is usually required
  after a plugin update.
- If the workflow fails after creating the release but before updating the manifest, re-run
  the failed job; the manifest step is idempotent and replaces the entry with the same version.

## Versioning

- `MAJOR.MINOR.PATCH.BUILD` (Jellyfin's four-part version).
- Patch releases for fixes and merges; bump the minor when adding user-visible features.
- The catalog selects the newest version whose `targetAbi` the server supports.

## Local package check

To reproduce the CI package without publishing a release:

```bash
version=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' Directory.Build.props | head -1)
dotnet publish Jellyfin.Plugin.AnimeMultiSource/Jellyfin.Plugin.AnimeMultiSource.csproj \
  -c Release -o "publish/$version"
(cd "publish/$version" && zip -q -r "../../AnimeMultiSource_v$version.zip" .)
md5sum "AnimeMultiSource_v$version.zip"
```
