#!/usr/bin/env bash

set -euo pipefail

readonly SUBJECT_PATTERN='^(feat|fix|docs|refactor|test|build|ci|chore|perf|style|revert)(\([a-z0-9][a-z0-9._/-]*\))?!?: [a-z0-9][^[:cntrl:]]+$'

validate_subject() {
    local subject="$1"
    [[ "$subject" =~ $SUBJECT_PATTERN ]]
}

self_test() {
    local valid_subjects=(
        'feat: add health endpoint'
        'fix(database): reject stale writes'
        'docs(api)!: remove legacy route'
        'revert: restore previous behavior'
    )
    local invalid_subjects=(
        'updated files'
        'Feat: add health endpoint'
        'fix(database) reject stale writes'
        'fix(Database): reject stale writes'
        'fix: Reject stale writes'
    )

    local subject
    for subject in "${valid_subjects[@]}"; do
        validate_subject "$subject" || {
            echo "Expected valid subject to pass: $subject" >&2
            return 1
        }
    done
    for subject in "${invalid_subjects[@]}"; do
        if validate_subject "$subject"; then
            echo "Expected invalid subject to fail: $subject" >&2
            return 1
        fi
    done
    echo "Commit subject policy self-test passed."
}

if [[ "${1:-}" == '--self-test' ]]; then
    self_test
    exit 0
fi
if [[ $# -ne 2 ]]; then
    echo "Usage: $0 <base-sha> <head-sha>" >&2
    exit 2
fi

base_sha="$1"
head_sha="$2"
zero_sha='0000000000000000000000000000000000000000'
if [[ -z "$base_sha" || "$base_sha" == "$zero_sha" ]]; then
    if git rev-parse "${head_sha}^" >/dev/null 2>&1; then
        range="${head_sha}^..${head_sha}"
    else
        range="$head_sha"
    fi
else
    range="${base_sha}..${head_sha}"
fi

mapfile -t commits < <(git rev-list --reverse "$range")
if [[ ${#commits[@]} -eq 0 ]]; then
    echo "No commits to validate in $range."
    exit 0
fi

failed=0
for commit in "${commits[@]}"; do
    subject="$(git show -s --format=%s "$commit")"
    if validate_subject "$subject"; then
        echo "OK ${commit:0:12} $subject"
    else
        echo "INVALID ${commit:0:12} $subject" >&2
        failed=1
    fi
done
if [[ $failed -ne 0 ]]; then
    echo "Commit subjects must match: <type>(<optional-scope>): <lowercase imperative summary>" >&2
    exit 1
fi
