#!/usr/bin/env bash

set -euo pipefail

DRY_RUN=false
if [[ $# -gt 1 ]]; then
    printf 'Usage: %s [--dry-run]\n' "$0"
    exit 1
fi
if [[ $# -eq 1 ]]; then
    [[ "$1" == "--dry-run" ]] || {
        printf 'Usage: %s [--dry-run]\n' "$0"
        exit 1
    }
    DRY_RUN=true
fi

fail() {
    printf 'Error: %s\n' "$1" >&2
    exit 1
}

command -v git >/dev/null 2>&1 || fail "git is required"

ROOT_DIR="$(git rev-parse --show-toplevel 2>/dev/null)" || \
    fail "run this script from inside the source repository"
cd "$ROOT_DIR"

CURRENT_BRANCH="$(git branch --show-current)"
[[ -n "$CURRENT_BRANCH" ]] || fail "detached HEAD is not allowed"

if [[ -n "$(git status --porcelain)" ]]; then
    if [[ "$DRY_RUN" == true ]]; then
        printf 'Warning: dry run is using a dirty working tree.\n'
    else
        fail "working tree is not clean"
    fi
fi

git show-ref --verify --quiet refs/heads/master || fail "local master branch does not exist"
git show-ref --verify --quiet refs/heads/dev || fail "local dev branch does not exist"
git show-ref --verify --quiet refs/heads/testing || fail "local testing branch does not exist"

git fetch origin master dev testing

git merge-base --is-ancestor origin/dev origin/master || \
    fail "origin/dev is not merged into origin/master"
git merge-base --is-ancestor origin/testing origin/master || \
    fail "origin/testing is not merged into origin/master"
git merge-base --is-ancestor master origin/master || \
    fail "local master contains commits not present on origin/master"
git merge-base --is-ancestor dev origin/master || \
    fail "local dev contains commits not present on origin/master"
git merge-base --is-ancestor testing origin/master || \
    fail "local testing contains commits not present on origin/master"

printf '\nBranch alignment plan:\n'
printf '  Remote dev:     fast-forward to origin/master\n'
printf '  Remote testing: fast-forward to origin/master\n'
printf '  Local master:   fast-forward to origin/master\n'
printf '  Local dev:      fast-forward to origin/dev\n'
printf '  Local testing:  fast-forward to origin/testing\n'
printf '  Return to:      %s\n' "$CURRENT_BRANCH"

if [[ "$DRY_RUN" == true ]]; then
    printf '\nDry run complete. No branches were switched or pushed.\n'
    exit 0
fi

restore_branch() {
    local branch
    branch="$(git branch --show-current)"
    if [[ "$branch" != "$CURRENT_BRANCH" ]]; then
        git switch "$CURRENT_BRANCH" >/dev/null 2>&1 || true
    fi
}
trap restore_branch EXIT

git push origin origin/master:dev origin/master:testing

git switch master
git merge --ff-only origin/master
git switch dev
git fetch origin dev
git merge --ff-only origin/dev
git switch testing
git fetch origin testing
git merge --ff-only origin/testing
git switch "$CURRENT_BRANCH"

printf '\nBranch alignment complete.\n'
