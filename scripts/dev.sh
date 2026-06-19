#!/usr/bin/env bash

if [ -z "${BASH_VERSION:-}" ]; then
  if command -v bash >/dev/null 2>&1; then
    exec bash "$0" "$@"
  fi

  echo "Error: bash is required to run this script." >&2
  exit 1
fi

set -euo pipefail

PROJECT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DOTNET_BIN="${DOTNET_BIN:-}"
DEV_URL="${DEV_URL:-http://127.0.0.1:5080}"
DEV_STORAGE_ROOT="${DEV_STORAGE_ROOT:-$PROJECT_DIR/data}"
DEV_TEMP_ROOT="${DEV_TEMP_ROOT:-$PROJECT_DIR/data/tmp}"
DEV_UPDATE_ROOT="${DEV_UPDATE_ROOT:-$PROJECT_DIR/data/updates}"
DEV_UPDATE_APPLY_ROOT="${DEV_UPDATE_APPLY_ROOT:-$PROJECT_DIR}"
DEV_API_KEY="${DEV_API_KEY:-dev-secret}"

usage() {
  cat <<USAGE
Usage: $0 <command>

Commands:
  server                         Start the backend dev server
  pack --git <range> [-o file]    Package files changed in a git range
  pack --files <paths...> [-o file]
                                 Package manually specified files

Examples:
  $0 server
  $0 pack --git HEAD~1..HEAD
  $0 pack --files Program.cs src/Updates/UpdateEndpoints.cs -o /tmp/update.zip
USAGE
}

log() {
  printf '[%s] %s\n' "$(date +'%Y-%m-%d %H:%M:%S')" "$*"
}

fail() {
  printf 'Error: %s\n' "$*" >&2
  exit 1
}

dotnet_cmd() {
  if [ -n "$DOTNET_BIN" ]; then
    printf '%s\n' "$DOTNET_BIN"
    return
  fi

  if command -v dotnet >/dev/null 2>&1; then
    command -v dotnet
    return
  fi

  if [ -x /tmp/dotnet/dotnet ]; then
    printf '%s\n' /tmp/dotnet/dotnet
    return
  fi

  fail "dotnet was not found"
}

ensure_package() {
  if [ -x "$PROJECT_DIR/package/musicdecrypto" ]; then
    return
  fi

  log "Extracting local package"
  PACKAGE_DIR="$PROJECT_DIR/package" "$PROJECT_DIR/scripts/manage.sh" extract-package
}

cmd_server() {
  ensure_package

  local dotnet_path
  dotnet_path="$(dotnet_cmd)"

  log "Starting backend on $DEV_URL"
  DOTNET_ROOT="$(dirname "$dotnet_path")" \
  DOTNET_CLI_HOME="${DOTNET_CLI_HOME:-/tmp/dotnet-cli-home}" \
  MusicDecrypto__StorageRoot="$DEV_STORAGE_ROOT" \
  MusicDecrypto__TempRoot="$DEV_TEMP_ROOT" \
  MusicDecrypto__UpdateRoot="$DEV_UPDATE_ROOT" \
  MusicDecrypto__UpdateApplyRoot="$DEV_UPDATE_APPLY_ROOT" \
  MusicDecrypto__ApiKey="$DEV_API_KEY" \
  Kestrel__Endpoints__Http__Url="$DEV_URL" \
  "$dotnet_path" run --project "$PROJECT_DIR/MusicDecrypto.Backend.csproj"
}

git_files() {
  local range="$1"
  git -C "$PROJECT_DIR" diff --name-only --diff-filter=ACMRT "$range"
}

manual_files() {
  printf '%s\n' "$@"
}

normalize_files() {
  local path
  while IFS= read -r path; do
    [ -n "$path" ] || continue
    path="${path#./}"
    [ -f "$PROJECT_DIR/$path" ] || fail "file not found: $path"
    case "$path" in
      /*|*../*|../*) fail "invalid file path: $path" ;;
    esac
    printf '%s\n' "$path"
  done | sort -u
}

write_manifest() {
  local staging_dir="$1"
  local manifest_path="$staging_dir/musicdecrypto-update.json"
  local first=1
  local path

  {
    printf '{\n'
    printf '  "format": "musicdecrypto.update.v1",\n'
    printf '  "createdAt": "%s",\n' "$(date -u +'%Y-%m-%dT%H:%M:%SZ')"
    printf '  "files": [\n'
    while IFS= read -r path; do
      local source_path="$PROJECT_DIR/$path"
      local size
      local sha256
      size="$(wc -c <"$source_path" | tr -d ' ')"
      sha256="$(sha256sum "$source_path" | awk '{print $1}')"

      if [ "$first" -eq 0 ]; then
        printf ',\n'
      fi
      first=0

      printf '    {\n'
      printf '      "path": "%s",\n' "$(json_escape "$path")"
      printf '      "source": "files/%s",\n' "$(json_escape "$path")"
      printf '      "size": %s,\n' "$size"
      printf '      "sha256": "%s"\n' "$sha256"
      printf '    }'
    done
    printf '\n  ]\n'
    printf '}\n'
  } >"$manifest_path"
}

json_escape() {
  local value="$1"
  value="${value//\\/\\\\}"
  value="${value//\"/\\\"}"
  printf '%s' "$value"
}

cmd_pack() {
  local output=""
  local mode=""
  local git_range=""
  local files=()

  while [ "$#" -gt 0 ]; do
    case "$1" in
      --git)
        mode="git"
        git_range="${2:-}"
        [ -n "$git_range" ] || fail "--git requires a range"
        shift 2
        ;;
      --files)
        mode="files"
        shift
        while [ "$#" -gt 0 ] && [[ "$1" != -* ]]; do
          files+=("$1")
          shift
        done
        ;;
      -o|--output)
        output="${2:-}"
        [ -n "$output" ] || fail "$1 requires a path"
        shift 2
        ;;
      *)
        fail "unknown pack argument: $1"
        ;;
    esac
  done

  [ -n "$mode" ] || fail "pack requires --git or --files"
  if [ -z "$output" ]; then
    output="$PROJECT_DIR/update-$(date -u +'%Y%m%d%H%M%S').zip"
  fi

  local file_list
  if [ "$mode" = "git" ]; then
    file_list="$(git_files "$git_range" | normalize_files)"
  else
    file_list="$(manual_files "${files[@]}" | normalize_files)"
  fi

  [ -n "$file_list" ] || fail "no files to package"

  local staging_dir
  staging_dir="$(mktemp -d)"
  trap "rm -rf '$staging_dir'" EXIT
  mkdir -p "$staging_dir/files"

  while IFS= read -r path; do
    mkdir -p "$staging_dir/files/$(dirname "$path")"
    cp "$PROJECT_DIR/$path" "$staging_dir/files/$path"
  done <<<"$file_list"

  write_manifest "$staging_dir" <<<"$file_list"

  mkdir -p "$(dirname "$output")"
  if command -v zip >/dev/null 2>&1; then
    (
      cd "$staging_dir"
      zip -qr "$output" musicdecrypto-update.json files
    )
  elif command -v python3 >/dev/null 2>&1; then
    python3 - "$staging_dir" "$output" <<'PY'
import os
import sys
import zipfile

staging_dir, output = sys.argv[1], sys.argv[2]
with zipfile.ZipFile(output, "w", compression=zipfile.ZIP_DEFLATED) as archive:
    for root, _, files in os.walk(staging_dir):
        for name in files:
            path = os.path.join(root, name)
            archive.write(path, os.path.relpath(path, staging_dir))
PY
  else
    fail "zip or python3 is required to create update packages"
  fi

  log "Created update package: $output"
  if command -v unzip >/dev/null 2>&1; then
    unzip -l "$output" | sed -n '1,40p'
  elif command -v python3 >/dev/null 2>&1; then
    python3 - "$output" <<'PY'
import sys
import zipfile

with zipfile.ZipFile(sys.argv[1]) as archive:
    for info in archive.infolist()[:40]:
        print(f"{info.file_size:8d} {info.filename}")
PY
  fi
}

command="${1:-}"
case "$command" in
  server)
    shift
    cmd_server "$@"
    ;;
  pack)
    shift
    cmd_pack "$@"
    ;;
  ""|-h|--help|help)
    usage
    ;;
  *)
    usage >&2
    fail "unknown command: $command"
    ;;
esac
