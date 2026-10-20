#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$root"

fail() {
  echo "::error::$*" >&2
  exit 1
}

prop() {
  sed -n "s:.*<$1>\\(.*\\)</$1>.*:\\1:p" Directory.Build.props | head -1
}

version="$(prop Version)"
assembly_version="$(prop AssemblyVersion)"
file_version="$(prop FileVersion)"
build_version="$(sed -n 's/^version: "\(.*\)"/\1/p' build.yaml | head -1)"
plugin_version="$(jq -r .version Jellyfin.Plugin.AnimeMultiSource/plugin.json)"
plugin_guid="$(jq -r .guid Jellyfin.Plugin.AnimeMultiSource/plugin.json)"
plugin_abi="$(jq -r .targetAbi Jellyfin.Plugin.AnimeMultiSource/plugin.json)"
build_guid="$(sed -n 's/^guid: "\(.*\)"/\1/p' build.yaml | head -1)"
build_abi="$(sed -n 's/^targetAbi: "\(.*\)"/\1/p' build.yaml | head -1)"
build_framework="$(sed -n 's/^framework: "\(.*\)"/\1/p' build.yaml | head -1)"
target_framework="$(sed -n 's:.*<TargetFramework>\(.*\)</TargetFramework>.*:\1:p' Jellyfin.Plugin.AnimeMultiSource/Jellyfin.Plugin.AnimeMultiSource.csproj | head -1)"

for pair in \
  "Directory.Build.props Version=$version" \
  "Directory.Build.props AssemblyVersion=$assembly_version" \
  "Directory.Build.props FileVersion=$file_version" \
  "build.yaml version=$build_version" \
  "plugin.json version=$plugin_version" \
  "plugin.json guid=$plugin_guid" \
  "plugin.json targetAbi=$plugin_abi" \
  "build.yaml guid=$build_guid" \
  "build.yaml targetAbi=$build_abi" \
  "build.yaml framework=$build_framework" \
  "csproj TargetFramework=$target_framework"; do
  name="${pair%%=*}"
  value="${pair#*=}"
  if [[ -z "$value" ]]; then
    fail "Could not read $name"
  fi
done

if [[ -n "${TAG_VERSION:-}" ]]; then
  tag_version="${TAG_VERSION#v}"
  if [[ "$tag_version" != "$version" ]]; then
    fail "Tag '$tag_version' must match Directory.Build.props version '$version'"
  fi
fi

if [[ "$version" != "$assembly_version" || "$version" != "$file_version" ]]; then
  fail "Version '$version' must match AssemblyVersion '$assembly_version' and FileVersion '$file_version'"
fi

if [[ "$version" != "$build_version" || "$version" != "$plugin_version" ]]; then
  fail "Version mismatch: Directory.Build.props='$version' build.yaml='$build_version' plugin.json='$plugin_version'"
fi

if [[ "$plugin_guid" != "$build_guid" ]]; then
  fail "GUID mismatch: plugin.json='$plugin_guid' build.yaml='$build_guid'"
fi

if [[ "$plugin_abi" != "$build_abi" ]]; then
  fail "targetAbi mismatch: plugin.json='$plugin_abi' build.yaml='$build_abi'"
fi

if [[ "$build_framework" != "$target_framework" ]]; then
  fail "Framework mismatch: build.yaml='$build_framework' csproj='$target_framework'"
fi

manifest_guid="$(jq -r '.[0].guid // empty' manifest.json)"
if [[ "$manifest_guid" != "$plugin_guid" ]]; then
  fail "manifest.json GUID '$manifest_guid' does not match plugin.json '$plugin_guid'"
fi

jq -e '
  type == "array" and length > 0
  and all(.[];
    (.guid | type == "string" and length > 0)
    and (.name | type == "string" and length > 0)
    and (.owner | type == "string" and length > 0)
    and (.category | type == "string" and length > 0)
    and (.versions | type == "array" and length > 0)
    and all(.versions[];
      (.version | type == "string" and length > 0)
      and (.targetAbi | type == "string" and length > 0)
      and (.sourceUrl | type == "string" and startswith("https://"))
      and (.changelog | type == "string" and startswith("https://"))
      and (.checksum | type == "string" and test("^[0-9A-Fa-f]{32}$"))
      and (.timestamp | type == "string" and length > 0)
    )
  )
' manifest.json >/dev/null || fail "manifest.json failed schema validation"

duplicates="$(jq -r '[.[].versions[].version] | group_by(.)[] | select(length > 1) | .[0]' manifest.json)"
if [[ -n "$duplicates" ]]; then
  fail "manifest.json has duplicate versions: $duplicates"
fi

if [[ "${REQUIRE_MANIFEST_VERSION:-0}" == "1" ]]; then
  jq -e --arg version "$version" '.[0].versions | any(.version == $version)' manifest.json >/dev/null \
    || fail "manifest.json does not contain version '$version'"
fi

echo "Metadata OK for $version"
