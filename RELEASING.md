# Releasing Anime Multi Source

Releases are created by GitHub Actions from an annotated tag. Do not build or upload the
zip by hand; the workflow does it and keeps `manifest.json` in sync.

## Checklist

1. Merge the changes to `main` and make sure CI is green.
2. Bump the version in **all five** values (they must match):
   - `Directory.Build.props`: `Version`, `AssemblyVersion`, and `FileVersion`
   - `build.yaml`: `version`
   - `Jellyfin.Plugin.AnimeMultiSource/plugin.json`: `version`
3. Add a `## <version> - <date>` section to `CHANGELOG.md`.
4. Build and smoke-test locally:

   ```bash
   dotnet build jellyfin-plugin-AnimeMultiSource.sln -c Release
   dotnet publish Jellyfin.Plugin.AnimeMultiSource/Jellyfin.Plugin.AnimeMultiSource.csproj -c Release -o publish
   bash .github/scripts/create-meta-json.sh
   ```

   `publish/` must contain `Jellyfin.Plugin.AnimeMultiSource.dll`, `plugin.json`, and
   `meta.json`, and must **not** contain `MediaBrowser.*.dll`, `Emby.*.dll`, or Jellyfin
   server assemblies.
5. Commit the version bump and changelog.
6. Tag and push:

   ```bash
   git tag -a v1.1.0.3 -m "Anime Multi Source 1.1.0.3"
   git push origin main
   git push origin v1.1.0.3
   ```

The **Release** workflow then:

- verifies the tag matches all version fields and cross-checks GUID, target ABI, and framework;
- builds, publishes, and validates the package;
- creates the GitHub release and uploads `AnimeMultiSource_v<version>.zip`;
- computes the MD5 checksum and prepends the release to `manifest.json` on `main`, keeping
  older catalog entries;
- commits the manifest update back to `main`.

## After the workflow

- Confirm the release exists and the manifest on `main` lists the new version and checksum.
- In Jellyfin, refresh the catalog and install/upgrade. A server restart is usually required
  after a plugin update.
- A published version is immutable. If the workflow fails after creating the release but
  before updating the manifest, fix the manifest manually or bump to the next patch version;
  re-running the same tag is rejected so an existing asset is never silently replaced.

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
