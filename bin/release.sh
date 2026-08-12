#!/usr/bin/env bash

set -euo pipefail

SOURCE_REPO="baanderson40/Retainer-Repricer"
MANIFEST_REPO="baanderson40/dalamud_plugins"
MANIFEST_BRANCH="main"
PROJECT_FILE="RetainerRepricer/RetainerRepricer.csproj"
MANIFEST_FILE="repo.json"
RELEASE_WORKFLOW="release.yml"

DRY_RUN=false
VERSION=""

usage() {
    printf 'Usage: %s <version> [--dry-run]\n' "$0"
    printf 'Example: %s 1.2.3.3\n' "$0"
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
    fail "version must contain exactly four numeric parts, for example 1.2.3.3"

command -v git >/dev/null 2>&1 || fail "git is required"
command -v gh >/dev/null 2>&1 || fail "GitHub CLI (gh) is required"
command -v jq >/dev/null 2>&1 || fail "jq is required; install it from https://jqlang.github.io/jq/download/"

ROOT_DIR="$(git rev-parse --show-toplevel 2>/dev/null)" || \
    fail "run this script from inside the source repository"
cd "$ROOT_DIR"

if [[ -n "$(git status --porcelain)" ]]; then
    if [[ "$DRY_RUN" == true ]]; then
        printf 'Warning: dry run is using a dirty working tree.\n'
    else
        fail "working tree is not clean"
    fi
fi

CURRENT_BRANCH="$(git branch --show-current)"
[[ -n "$CURRENT_BRANCH" ]] || fail "detached HEAD is not allowed"
if [[ "$DRY_RUN" != true && "$CURRENT_BRANCH" != "master" ]]; then
    fail "normal releases must run from master (currently on '$CURRENT_BRANCH')"
fi

if [[ "$DRY_RUN" != true ]]; then
    gh auth status >/dev/null 2>&1 || fail "GitHub CLI is not authenticated"
    git show-ref --verify --quiet refs/heads/dev || fail "local dev branch does not exist"
    git fetch origin master dev
    read -r REMOTE_ONLY LOCAL_ONLY < <(git rev-list --left-right --count origin/master...HEAD)
    [[ "$LOCAL_ONLY" -eq 0 ]] || fail "master has unpushed commits; push or resolve them before releasing"
    if [[ "$REMOTE_ONLY" -gt 0 ]]; then
        printf 'Fast-forwarding master to origin/master...\n'
        git merge --ff-only origin/master
    fi
    if ! git merge-base --is-ancestor origin/dev origin/master; then
        PR_NUMBER="$(gh pr list \
            --repo "$SOURCE_REPO" \
            --base master \
            --head dev \
            --state open \
            --json number \
            --jq '.[0].number // empty')"
        [[ -n "$PR_NUMBER" ]] || \
            fail "dev is ahead of master and has no open dev-to-master pull request"

        printf 'Waiting for checks on pull request #%s...\n' "$PR_NUMBER"
        gh pr checks "$PR_NUMBER" --repo "$SOURCE_REPO" --watch || \
            fail "checks failed for pull request #$PR_NUMBER"

        printf 'Merging pull request #%s...\n' "$PR_NUMBER"
        gh pr merge "$PR_NUMBER" \
            --repo "$SOURCE_REPO" \
            --merge \
            --delete-branch=false || \
            fail "could not merge pull request #$PR_NUMBER"

        PR_STATE=""
        for _ in {1..60}; do
            PR_STATE="$(gh pr view "$PR_NUMBER" \
                --repo "$SOURCE_REPO" \
                --json state \
                --jq '.state' 2>/dev/null || true)"
            [[ "$PR_STATE" == "MERGED" ]] && break
            sleep 5
        done
        [[ "$PR_STATE" == "MERGED" ]] || \
            fail "pull request #$PR_NUMBER was not merged"

        git fetch origin master dev
        git merge --ff-only origin/master
    fi
    git merge-base --is-ancestor origin/dev origin/master || \
        fail "origin/dev is not merged into origin/master after pull request"
    git merge-base --is-ancestor dev origin/master || \
        fail "local dev contains commits not merged into origin/master"
else
    printf 'Dry run on branch %s; no repository changes will be made.\n' "$CURRENT_BRANCH"
    printf 'Branch alignment: master will follow origin/master; dev will follow master after release.\n'
fi

CURRENT_SOURCE_VERSION="$(sed -n 's:.*<Version>\([0-9][0-9.]*\)</Version>.*:\1:p' "$PROJECT_FILE" | head -n 1)"
[[ -n "$CURRENT_SOURCE_VERSION" ]] || fail "could not read the version from $PROJECT_FILE"

TEMP_DIR=""
cleanup() {
    if [[ -n "$TEMP_DIR" && -d "$TEMP_DIR" ]]; then
        rm -rf "$TEMP_DIR"
    fi
}
trap cleanup EXIT

TEMP_DIR="$(mktemp -d "${TMPDIR:-/tmp}/retainer-repricer-release.XXXXXX")"
gh repo clone "$MANIFEST_REPO" "$TEMP_DIR/manifest" -- --branch "$MANIFEST_BRANCH" >/dev/null

MANIFEST_PATH="$TEMP_DIR/manifest/$MANIFEST_FILE"
[[ -f "$MANIFEST_PATH" ]] || fail "$MANIFEST_FILE was not found in $MANIFEST_REPO"

MANIFEST_DATA="$(jq -er '
    map(select(.InternalName == "RetainerRepricer"))
    | if length != 1 then error("expected exactly one RetainerRepricer manifest entry") else .[0] end
    | [.AssemblyVersion, .DownloadLinkInstall, (.DownloadCount // 0)]
    | @tsv
' "$MANIFEST_PATH")" || fail "could not read the RetainerRepricer manifest entry"
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
version_is_less "$VERSION" "$CURRENT_SOURCE_VERSION" && \
    fail "version $VERSION is older than the source version $CURRENT_SOURCE_VERSION"
[[ "$VERSION" != "$MANIFEST_VERSION" ]] || \
    fail "version $VERSION is already the published manifest version"

NEW_TAG="v$VERSION"
if git rev-parse --verify --quiet "refs/tags/$NEW_TAG" >/dev/null; then
    fail "tag $NEW_TAG already exists locally"
fi
if git ls-remote --exit-code --refs origin "refs/tags/$NEW_TAG" >/dev/null 2>&1; then
    fail "tag $NEW_TAG already exists on origin"
fi
gh release view "$NEW_TAG" --repo "$SOURCE_REPO" >/dev/null 2>&1 && \
    fail "GitHub release $NEW_TAG already exists"

DOWNLOAD_COUNT="$CURRENT_MANIFEST_COUNT"
PREVIOUS_DOWNLOAD_COUNT="$(gh release view "$PREVIOUS_TAG" \
    --repo "$SOURCE_REPO" \
    --json assets \
    --jq '.assets[] | select(.name == "latest.zip") | .downloadCount' \
    2>/dev/null || true)"
if [[ "$PREVIOUS_DOWNLOAD_COUNT" =~ ^[0-9]+$ ]] \
    && (( PREVIOUS_DOWNLOAD_COUNT > DOWNLOAD_COUNT )); then
    DOWNLOAD_COUNT="$PREVIOUS_DOWNLOAD_COUNT"
fi

NEW_DOWNLOAD_URL="https://github.com/$SOURCE_REPO/releases/download/$NEW_TAG/latest.zip"

printf '\nRelease plan:\n'
printf '  Source branch:       %s\n' "$CURRENT_BRANCH"
printf '  Source version:      %s -> %s\n' "$CURRENT_SOURCE_VERSION" "$VERSION"
printf '  Git tag:             %s\n' "$NEW_TAG"
printf '  Previous release:    %s\n' "$PREVIOUS_TAG"
printf '  Manifest count:      %s\n' "$DOWNLOAD_COUNT"
printf '  New download URL:    %s\n' "$NEW_DOWNLOAD_URL"

if [[ "$DRY_RUN" == true ]]; then
    jq --arg version "$VERSION" \
       --arg url "$NEW_DOWNLOAD_URL" \
       --argjson count "$DOWNLOAD_COUNT" \
       'map(if .InternalName == "RetainerRepricer" then
           .AssemblyVersion = $version
           | .DownloadLinkInstall = $url
           | .DownloadLinkUpdate = $url
           | .DownloadCount = $count
       else . end)' "$MANIFEST_PATH" > "$TEMP_DIR/repo.updated.json"
    printf '\nDry run complete. No commits, tags, pushes, releases, or manifest changes were made.\n'
    exit 0
fi

if [[ "$CURRENT_SOURCE_VERSION" != "$VERSION" ]]; then
    sed -i "0,/<Version>[0-9.]*<\/Version>/{s/<Version>[0-9.]*<\/Version>/<Version>$VERSION<\/Version>/}" "$PROJECT_FILE"
    git add "$PROJECT_FILE"
    git commit -m "Version: $VERSION"
    git restore --worktree -- "$PROJECT_FILE"
    git push origin master
fi

git tag -a "$NEW_TAG" -m "$NEW_TAG"
git push origin "$NEW_TAG"

printf 'Waiting for GitHub Actions workflow %s...\n' "$RELEASE_WORKFLOW"
COMMIT_SHA="$(git rev-parse HEAD)"
RUN_ID=""
for _ in {1..60}; do
    RUN_ID="$(gh run list \
        --repo "$SOURCE_REPO" \
        --workflow "$RELEASE_WORKFLOW" \
        --commit "$COMMIT_SHA" \
        --json databaseId \
        --jq '.[0].databaseId // empty' 2>/dev/null || true)"
    [[ -n "$RUN_ID" ]] && break
    sleep 5
done
[[ -n "$RUN_ID" ]] || fail "could not find the release workflow run for $NEW_TAG"
gh run watch "$RUN_ID" --repo "$SOURCE_REPO" --exit-status

for _ in {1..60}; do
    if gh release view "$NEW_TAG" --repo "$SOURCE_REPO" \
        --json assets \
        --jq '.assets[] | select(.name == "latest.zip") | .name' \
        2>/dev/null | grep -qx 'latest.zip'; then
        break
    fi
    sleep 5
done
gh release view "$NEW_TAG" --repo "$SOURCE_REPO" \
    --json assets \
    --jq '.assets[] | select(.name == "latest.zip") | .name' \
    2>/dev/null | grep -qx 'latest.zip' || \
    fail "release $NEW_TAG completed without latest.zip"

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
    git -C "$TEMP_DIR/manifest" commit -m "Update RetainerRepricer to $VERSION"
    git -C "$TEMP_DIR/manifest" push origin "$MANIFEST_BRANCH"
fi

printf 'Aligning dev with master...\n'
git fetch origin master dev
git push origin origin/master:dev
git fetch origin dev
git switch dev
git merge --ff-only origin/dev
git switch master

printf 'Release %s complete.\n' "$VERSION"
