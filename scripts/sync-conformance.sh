#!/usr/bin/env bash
# Vendors the finlight API v3 contract kit into ./conformance and checks it.
#
#   scripts/sync-conformance.sh                 copy from the default kit location
#   scripts/sync-conformance.sh --from <dir>    copy from <dir> (or set FINLIGHT_CONTRACT_KIT)
#   scripts/sync-conformance.sh --check         verify ./conformance against its MANIFEST
#
# The default location is the monorepo layout this repo is a submodule of:
# <monorepo>/shared/csharp/packages/finlight-client -> <monorepo>/shared/contracts/api-v3.
#
# A copy is only accepted when every file matches the sha256 in the kit's MANIFEST. The check
# fails when a file is missing, changed, or not listed. After a sync, regenerate the vocabulary:
#   python3 scripts/generate-vocabulary.py conformance/vocab.json src/Finlight.Client/Vocabulary.g.cs
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
TARGET="$ROOT/conformance"
DEFAULT_SOURCE="$ROOT/../../../contracts/api-v3"

fail() { echo "conformance: $*" >&2; exit 1; }

sha256_of() {
  if command -v sha256sum >/dev/null 2>&1; then sha256sum "$1" | cut -d' ' -f1
  else shasum -a 256 "$1" | cut -d' ' -f1; fi
}

# verify <dir>: every MANIFEST entry exists with its checksum, every file is listed.
verify() {
  local dir="$1" problems=0 hash rel
  [ -f "$dir/MANIFEST" ] || fail "no MANIFEST in $dir"
  grep -q '  CONTRACT_VERSION$' "$dir/MANIFEST" || fail "MANIFEST does not list CONTRACT_VERSION"
  while IFS= read -r line; do
    [ -z "$line" ] && continue
    hash="${line%%  *}"
    rel="${line#*  }"
    if [ ! -f "$dir/$rel" ]; then echo "  missing $rel" >&2; problems=1
    elif [ "$(sha256_of "$dir/$rel")" != "$hash" ]; then echo "  checksum mismatch $rel" >&2; problems=1
    fi
  done < "$dir/MANIFEST"
  while IFS= read -r file; do
    rel="${file#"$dir"/}"
    [ "$rel" = "MANIFEST" ] && continue
    grep -q "  $rel\$" "$dir/MANIFEST" || { echo "  not in MANIFEST $rel" >&2; problems=1; }
  done < <(find "$dir" -type f | sort)
  return $problems
}

case "${1:-}" in
  --check)
    [ -d "$TARGET" ] || fail "./conformance does not exist; run scripts/sync-conformance.sh"
    verify "$TARGET" || fail "./conformance does not match its MANIFEST"
    echo "conformance: ok, contract $(tr -d '\n' < "$TARGET/CONTRACT_VERSION"), $(grep -c . "$TARGET/MANIFEST") files"
    ;;
  --from|"")
    if [ "${1:-}" = "--from" ]; then SOURCE="${2:?--from needs a directory}"; else SOURCE="${FINLIGHT_CONTRACT_KIT:-$DEFAULT_SOURCE}"; fi
    [ -d "$SOURCE" ] || fail "kit not found at $SOURCE; pass --from <dir> or set FINLIGHT_CONTRACT_KIT"
    SOURCE="$(cd "$SOURCE" && pwd)"
    verify "$SOURCE" || fail "the kit at $SOURCE does not match its own MANIFEST"
    rm -rf "$TARGET"
    while IFS= read -r line; do
      [ -z "$line" ] && continue
      rel="${line#*  }"
      mkdir -p "$(dirname "$TARGET/$rel")"
      cp "$SOURCE/$rel" "$TARGET/$rel"
    done < "$SOURCE/MANIFEST"
    cp "$SOURCE/MANIFEST" "$TARGET/MANIFEST"
    verify "$TARGET" || fail "copy failed"
    echo "conformance: synced contract $(tr -d '\n' < "$TARGET/CONTRACT_VERSION") from $SOURCE"
    ;;
  *)
    fail "usage: scripts/sync-conformance.sh [--from <dir> | --check]"
    ;;
esac
