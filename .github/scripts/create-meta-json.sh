#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$root"

version="$(jq -r .version Jellyfin.Plugin.AnimeMultiSource/plugin.json)"
guid="$(jq -r .guid Jellyfin.Plugin.AnimeMultiSource/plugin.json)"
name="$(jq -r .name Jellyfin.Plugin.AnimeMultiSource/plugin.json)"
description="$(jq -r .description Jellyfin.Plugin.AnimeMultiSource/plugin.json)"
owner="$(jq -r .owner Jellyfin.Plugin.AnimeMultiSource/plugin.json)"
target_abi="$(jq -r .targetAbi Jellyfin.Plugin.AnimeMultiSource/plugin.json)"
overview="$(sed -n 's/^overview: "\(.*\)"/\1/p' build.yaml | head -1)"
category="$(sed -n 's/^category: "\(.*\)"/\1/p' build.yaml | head -1)"
timestamp="${META_TIMESTAMP:-$(date -u +%Y-%m-%dT%H:%M:%SZ)}"
repository="${GITHUB_REPOSITORY:-JohnnyTaxs/jellyfin-plugin-AnimeMultiSource}"
changelog="${META_CHANGELOG:-https://github.com/${repository}/releases/tag/v${version}}"

mkdir -p publish
jq -n \
  --arg guid "$guid" \
  --arg name "$name" \
  --arg description "$description" \
  --arg overview "$overview" \
  --arg owner "$owner" \
  --arg category "$category" \
  --arg targetAbi "$target_abi" \
  --arg timestamp "$timestamp" \
  --arg version "$version" \
  --arg changelog "$changelog" \
  '{
    guid: $guid,
    name: $name,
    description: $description,
    overview: $overview,
    owner: $owner,
    category: $category,
    targetAbi: $targetAbi,
    timestamp: $timestamp,
    version: $version,
    changelog: $changelog,
    assemblies: ["Jellyfin.Plugin.AnimeMultiSource.dll"]
  }' > publish/meta.json

jq -e '
  (.guid | type == "string" and length > 0)
  and (.name | type == "string" and length > 0)
  and (.owner | type == "string" and length > 0)
  and (.targetAbi | type == "string" and length > 0)
  and (.version | type == "string" and length > 0)
  and (.assemblies | type == "array" and length > 0)
' publish/meta.json >/dev/null

echo "Wrote publish/meta.json for $version"
