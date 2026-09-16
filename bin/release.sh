#!/usr/bin/env bash

set -euo pipefail

SOURCE_REPO="baanderson40/Retainer-Repricer"
MANIFEST_REPO="baanderson40/dalamud_plugins"
MANIFEST_BRANCH="main"
MANIFEST_FILE="repo.json"

DRY_RUN=false
VERSION=""

usage() {
    printf 'Usage: %s <version> [--dry-run]\n' "$0"
    printf 'Run this after GitHub has published v<version> with latest.zip.\n'
}

fail() {
    printf 'Error: %s\n' "$1" >&2
    exit 1
}

if [[ $# -lt 1 || $# -gt 2 ]]; then
    usage
    exit 1
fi

VERSION="$1"
if [[ $# -eq 2 ]]; then
    [[ "$2" == "--dry-run" ]] || fail "unknown option '$2'"
    DRY_RUN=true
fi

[[ "$VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$ ]] || \
    fail "version must contain exactly four numeric parts, for example 1.3.0.2"

command -v git >/dev/null 2>&1 || fail "git is required"
command -v gh >/dev/null 2>&1 || fail "GitHub CLI (gh) is required"
command -v jq >/dev/null 2>&1 || fail "jq is required; install it from https://jqlang.github.io/jq/download/"

ROOT_DIR="$(git rev-parse --show-toplevel 2>/dev/null)" || \
    fail "run this script from inside the source repository"
cd "$ROOT_DIR"

gh auth status >/dev/null 2>&1 || fail "GitHub CLI is not authenticated"

NEW_TAG="v$VERSION"
NEW_ASSET="$(gh release view "$NEW_TAG" \
    --repo "$SOURCE_REPO" \
    --json assets \
    --jq '.assets[] | select(.name == "latest.zip") | .name' \
    2>/dev/null || true)"
[[ "$NEW_ASSET" == "latest.zip" ]] || \
    fail "release $NEW_TAG does not have a latest.zip asset yet"

TEMP_DIR=""
cleanup() {
    if [[ -n "$TEMP_DIR" && -d "$TEMP_DIR" ]]; then
        rm -rf "$TEMP_DIR"
    fi
}
trap cleanup EXIT

TEMP_DIR="$(mktemp -d "${TMPDIR:-/tmp}/retainer-repricer-manifest.XXXXXX")"
gh repo clone "$MANIFEST_REPO" "$TEMP_DIR/manifest" -- --branch "$MANIFEST_BRANCH" >/dev/null

MANIFEST_PATH="$TEMP_DIR/manifest/$MANIFEST_FILE"
[[ -f "$MANIFEST_PATH" ]] || fail "$MANIFEST_FILE was not found in $MANIFEST_REPO"

MANIFEST_DATA="$(jq -er '
    map(select(.InternalName == "RetainerRepricer"))
    | if length != 1 then error("expected exactly one RetainerRepricer manifest entry") else .[0] end
    | [.AssemblyVersion, .DownloadLinkInstall, (.DownloadCount // 0)]
    | @tsv
' "$MANIFEST_PATH")" || fail "could not read the Retainer Repricer manifest entry"
IFS=$'\t' read -r MANIFEST_VERSION CURRENT_DOWNLOAD_URL CURRENT_MANIFEST_COUNT <<< "$MANIFEST_DATA"

[[ "$MANIFEST_VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$ ]] || \
    fail "manifest AssemblyVersion is invalid: $MANIFEST_VERSION"
[[ "$CURRENT_MANIFEST_COUNT" =~ ^[0-9]+$ ]] || \
    fail "manifest DownloadCount is invalid: $CURRENT_MANIFEST_COUNT"
[[ "$CURRENT_DOWNLOAD_URL" =~ /releases/download/([^/]+)/latest\.zip$ ]] || \
    fail "could not parse the current release tag from DownloadLinkInstall"
PREVIOUS_TAG="${BASH_REMATCH[1]}"

version_is_less() {
    [[ "$1" != "$2" ]] && \
        [[ "$(printf '%s\n%s\n' "$1" "$2" | sort -V | head -n 1)" == "$1" ]]
}

version_is_less "$VERSION" "$MANIFEST_VERSION" && \
    fail "version $VERSION is older than the manifest version $MANIFEST_VERSION"
[[ "$VERSION" != "$MANIFEST_VERSION" ]] || \
    fail "version $VERSION is already the published manifest version"

PREVIOUS_DOWNLOAD_COUNT="$(gh release view "$PREVIOUS_TAG" \
    --repo "$SOURCE_REPO" \
    --json assets \
    --jq '.assets[] | select(.name == "latest.zip") | .downloadCount' \
    2>/dev/null)" || fail "could not read download count for release $PREVIOUS_TAG"
[[ "$PREVIOUS_DOWNLOAD_COUNT" =~ ^[0-9]+$ ]] || \
    fail "release $PREVIOUS_TAG has no latest.zip download count"

DOWNLOAD_COUNT="$CURRENT_MANIFEST_COUNT"
if (( PREVIOUS_DOWNLOAD_COUNT > DOWNLOAD_COUNT )); then
    DOWNLOAD_COUNT="$PREVIOUS_DOWNLOAD_COUNT"
fi

NEW_DOWNLOAD_URL="https://github.com/$SOURCE_REPO/releases/download/$NEW_TAG/latest.zip"

printf '\nManifest update plan:\n'
printf '  Previous release:     %s\n' "$PREVIOUS_TAG"
printf '  Previous asset count: %s\n' "$PREVIOUS_DOWNLOAD_COUNT"
printf '  Version:              %s -> %s\n' "$MANIFEST_VERSION" "$VERSION"
printf '  Manifest count:       %s\n' "$DOWNLOAD_COUNT"
printf '  Download URL:         %s\n' "$NEW_DOWNLOAD_URL"

if [[ "$DRY_RUN" == true ]]; then
    printf '\nDry run complete. No manifest commits or pushes were made.\n'
    exit 0
fi

jq --arg version "$VERSION" \
   --arg url "$NEW_DOWNLOAD_URL" \
   --argjson count "$DOWNLOAD_COUNT" \
   'map(if .InternalName == "RetainerRepricer" then
        .AssemblyVersion = $version
        | .DownloadLinkInstall = $url
        | .DownloadLinkUpdate = $url
        | .DownloadCount = $count
    else . end)' "$MANIFEST_PATH" > "$TEMP_DIR/repo.updated.json"
mv "$TEMP_DIR/repo.updated.json" "$MANIFEST_PATH"

if git -C "$TEMP_DIR/manifest" diff --quiet -- "$MANIFEST_FILE"; then
    printf 'Manifest already contains the requested values.\n'
else
    git -C "$TEMP_DIR/manifest" add "$MANIFEST_FILE"
    git -C "$TEMP_DIR/manifest" commit -m "Update Retainer Repricer to $VERSION"
    git -C "$TEMP_DIR/manifest" push origin "$MANIFEST_BRANCH"
fi

printf 'Manifest update for %s complete.\n' "$VERSION"
