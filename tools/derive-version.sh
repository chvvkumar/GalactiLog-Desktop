#!/usr/bin/env bash
#
# Usage: derive-version.sh <branch>
# Reads the tag list from stdin, one tag per line.
# Writes three lines to stdout, in this order:
#   version=<x>
#   channel=<x>
#   prerelease=<true|false>
# Exits 1 with a message on stderr for an unrecognized branch.
#
# Mirrors design-spec.md 17.4, which itself mirrors the web repository's build-deploy.yml
# "Determine next version" step. One mechanism diverges from the web on purpose: the web sorts
# the whole prerelease tag with `sort -V` and then extracts the counter; this script strips the
# base and prerelease prefix first with `sed`, then sorts the bare integers with `sort -n`. Both
# are correct, but `sort -n` on extracted integers is the one that cannot be fooled by a locale
# (this is also why LC_ALL=C is exported below, for `sort` and `grep` alike).
#
# The script never calls git itself: tags arrive on stdin so this can be driven from a fixture
# tag list in a test, which is the whole reason this script exists as a separate file.

set -euo pipefail
export LC_ALL=C

BRANCH="${1:-}"

case "$BRANCH" in
  snd)  PRE=alpha ;;
  dev)  PRE=rc ;;
  main) PRE="" ;;
  *)
    echo "Unexpected branch: $BRANCH" >&2
    exit 1
    ;;
esac

# Read stdin exactly once. Both filters below run over this stored copy. A second read of stdin
# returns nothing and would silently produce 1.0.0 on every branch.
TAGS="$(cat)"

# Anchored at both ends: this is what excludes a prerelease tag such as 1.4.1-rc.3, and any tag
# not shaped like exactly MAJOR.MINOR.PATCH (a v-prefixed tag, a two-part tag, a four-part tag),
# from the stable set.
STABLE="$(printf '%s\n' "$TAGS" | grep -E '^[0-9]+\.[0-9]+\.[0-9]+$' | sort -V | tail -n 1 || true)"

if [ -z "$STABLE" ]; then
  BASE="1.0.0"
else
  IFS=. read -r MAJOR MINOR PATCH <<< "$STABLE"
  # Only the patch segment is incremented. Major and minor move only by a manually created tag.
  # 10#$PATCH forces base-10 arithmetic so a zero-padded patch segment (08) is read as eight
  # rather than rejected as an invalid octal digit.
  BASE="${MAJOR}.${MINOR}.$((10#$PATCH + 1))"
fi

# BASE is interpolated into the prerelease grep and sed patterns below. Escaping its dots keeps
# it a literal match rather than a regex wildcard: unescaped, base 1.4.1 would also match a tag
# such as 1x4x1-alpha.5, since "." matches any character.
BASE_RE="${BASE//./\\.}"

if [ -z "$PRE" ]; then
  VERSION="$BASE"
  CHANNEL="stable"
  PRERELEASE="false"
else
  # The counter restarts at 1 whenever the base changes: this needs no explicit reset code. A
  # new base has no tags matching "${BASE}-${PRE}.N" yet, so HIGHEST comes back empty and N is 1.
  HIGHEST="$(printf '%s\n' "$TAGS" | grep -E "^${BASE_RE}-${PRE}\.[0-9]+$" | sed -E "s/^${BASE_RE}-${PRE}\.//" | sort -n | tail -n 1 || true)"
  if [ -z "$HIGHEST" ]; then
    N=1
  else
    # 10#$HIGHEST for the same reason as the patch segment above: a hand-created tag with a
    # zero-padded counter (1.4.1-alpha.08) matches the grep, and without the prefix the
    # arithmetic rejects it as an invalid octal digit and set -e ends the release job with a
    # shell error instead of a message (phase review minor P8).
    N=$((10#$HIGHEST + 1))
  fi
  VERSION="${BASE}-${PRE}.${N}"
  CHANNEL="$PRE"
  PRERELEASE="true"
fi

# Nothing but these three lines goes to stdout: the workflow redirects stdout straight into
# GITHUB_OUTPUT. Any diagnostic belongs on stderr instead.
echo "version=$VERSION"
echo "channel=$CHANNEL"
echo "prerelease=$PRERELEASE"
