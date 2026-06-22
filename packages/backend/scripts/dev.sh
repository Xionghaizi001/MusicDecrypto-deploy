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
FRONTEND_PROJECT_DIR="${FRONTEND_PROJECT_DIR:-$(cd "$PROJECT_DIR/../frontend" && pwd)}"
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
  pack [-o file]                  Package latest commits from backend and frontend
  pack --git [commit|range] [-o file]
                                 Package files changed by a commit or git range
  pack --backend|--frontend [--git [commit|range]] [-o file]
                                 Limit git packaging to one repository
  pack --backend|--frontend --files <paths...> [-o file]
                                 Package manually specified files

Examples:
  $0 server
  $0 pack
  $0 pack --git HEAD~1..HEAD
  $0 pack --frontend --git HEAD
  $0 pack --backend --files Program.cs src/Updates/UpdateEndpoints.cs -o /tmp/update.zip
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

  fail "dotnet was not found. Install the .NET SDK normally or set DOTNET_BIN=/path/to/dotnet."
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
  MusicDecrypto__StorageRoot="$DEV_STORAGE_ROOT" \
  MusicDecrypto__TempRoot="$DEV_TEMP_ROOT" \
  MusicDecrypto__UpdateRoot="$DEV_UPDATE_ROOT" \
  MusicDecrypto__UpdateApplyRoot="$DEV_UPDATE_APPLY_ROOT" \
  MusicDecrypto__ApiKey="$DEV_API_KEY" \
  Kestrel__Endpoints__Http__Url="$DEV_URL" \
  "$dotnet_path" run --project "$PROJECT_DIR/MusicDecrypto.Backend.csproj"
}

repo_dir_for_target() {
  case "$1" in
    backend) printf '%s\n' "$PROJECT_DIR" ;;
    frontend) printf '%s\n' "$FRONTEND_PROJECT_DIR" ;;
    *) fail "unknown update target: $1" ;;
  esac
}

is_git_range() {
  [[ "$1" == *..* ]]
}

git_files_for_spec() {
  local target="$1"
  local spec="${2:-HEAD}"
  local repo
  repo="$(repo_dir_for_target "$target")"

  [ -d "$repo/.git" ] || fail "$target repository is not a git repository: $repo"

  if is_git_range "$spec"; then
    git -C "$repo" diff --name-only --diff-filter=ACMRT "$spec"
    return
  fi

  git -C "$repo" cat-file -e "$spec^{commit}"
  git -C "$repo" diff-tree --no-commit-id --name-only -r --root --diff-filter=ACMRT "$spec"
}

normalize_target_files() {
  local target="$1"
  local repo
  repo="$(repo_dir_for_target "$target")"

  local path
  while IFS= read -r path; do
    [ -n "$path" ] || continue
    path="${path#./}"
    [ -f "$repo/$path" ] || fail "$target file not found: $path"
    case "$path" in
      /*|*../*|../*) fail "invalid $target file path: $path" ;;
    esac
    printf '%s\t%s\n' "$target" "$path"
  done
}

manual_files() {
  local target="$1"
  shift
  printf '%s\n' "$@" | normalize_target_files "$target"
}

write_manifest() {
  local staging_dir="$1"
  local source_file="${2:-}"
  local manifest_path="$staging_dir/musicdecrypto-update.json"
  local first=1
  local target
  local path

  {
    printf '{\n'
    printf '  "format": "musicdecrypto.update.v1",\n'
    printf '  "createdAt": "%s",\n' "$(date -u +'%Y-%m-%dT%H:%M:%SZ')"
    if [ -n "$source_file" ] && [ -s "$source_file" ]; then
      write_git_source_json "$source_file"
      printf ',\n'
    fi
    printf '  "files": [\n'
    while IFS=$'\t' read -r target path; do
      local repo
      repo="$(repo_dir_for_target "$target")"
      local source_path="$repo/$path"
      local size
      local sha256
      size="$(wc -c <"$source_path" | tr -d ' ')"
      sha256="$(sha256sum "$source_path" | awk '{print $1}')"

      if [ "$first" -eq 0 ]; then
        printf ',\n'
      fi
      first=0

      printf '    {\n'
      printf '      "target": "%s",\n' "$(json_escape "$target")"
      printf '      "path": "%s",\n' "$(json_escape "$path")"
      printf '      "source": "files/%s/%s",\n' "$(json_escape "$target")" "$(json_escape "$path")"
      printf '      "size": %s,\n' "$size"
      printf '      "sha256": "%s"\n' "$sha256"
      printf '    }'
    done
    printf '\n  ]\n'
    printf '}\n'
  } >"$manifest_path"
}

write_git_source_json() {
  local source_file="$1"
  local summary_range="latest"
  local source_count
  source_count="$(wc -l <"$source_file" | tr -d ' ')"
  if [ "$source_count" -eq 1 ]; then
    summary_range="$(cut -f2 "$source_file")"
  fi

  printf '  "source": {\n'
  printf '    "type": "git",\n'
  printf '    "range": "%s",\n' "$(json_escape "$summary_range")"
  printf '    "commits": [\n'

  WRITE_COMMIT_FIRST=1
  local target
  local spec
  while IFS=$'\t' read -r target spec; do
    write_git_commit_items "$target" "$spec" "      "
  done <"$source_file"

  printf '\n'
  printf '    ],\n'
  printf '    "repositories": [\n'

  local first_repo=1
  while IFS=$'\t' read -r target spec; do
    local repo
    repo="$(repo_dir_for_target "$target")"
    if [ "$first_repo" -eq 0 ]; then
      printf ',\n'
    fi
    first_repo=0

    printf '      {\n'
    printf '        "target": "%s",\n' "$(json_escape "$target")"
    printf '        "path": "%s",\n' "$(json_escape "$repo")"
    printf '        "range": "%s",\n' "$(json_escape "$spec")"
    printf '        "commits": [\n'
    write_git_commit_array "$target" "$spec" "          "
    printf '\n'
    printf '        ]\n'
    printf '      }'
  done <"$source_file"

  printf '\n'
  printf '    ]\n'
  printf '  }'
}

write_git_commit_items() {
  local target="$1"
  local spec="$2"
  local indent="$3"
  local repo
  repo="$(repo_dir_for_target "$target")"
  local commit_format='%H%x1f%h%x1f%an%x1f%aI%x1f%s%x1f%b%x1e'
  local commits
  if is_git_range "$spec"; then
    commits="$(git -C "$repo" log --reverse --format="$commit_format" "$spec")"
  else
    commits="$(git -C "$repo" log -1 --format="$commit_format" "$spec")"
  fi

  local record
  while IFS= read -r -d $'\x1e' record; do
    [ -n "$record" ] || continue
    IFS=$'\x1f' read -r hash short_hash author authored_at subject body <<<"$record"

    if [ "$WRITE_COMMIT_FIRST" -eq 0 ]; then
      printf ',\n'
    fi
    WRITE_COMMIT_FIRST=0

    write_git_commit_object "$target" "$hash" "$short_hash" "$author" "$authored_at" "$subject" "$body" "$indent"
  done <<<"$commits"
}

write_git_commit_array() {
  local target="$1"
  local spec="$2"
  local indent="$3"
  WRITE_COMMIT_FIRST=1
  write_git_commit_items "$target" "$spec" "$indent"
}

write_git_commit_object() {
  local target="$1"
  local hash="$2"
  local short_hash="$3"
  local author="$4"
  local authored_at="$5"
  local subject="$6"
  local body="$7"
  local indent="$8"

  printf '%s{\n' "$indent"
  printf '%s  "target": "%s",\n' "$indent" "$(json_escape "$target")"
  printf '%s  "hash": "%s",\n' "$indent" "$(json_escape "$hash")"
  printf '%s  "shortHash": "%s",\n' "$indent" "$(json_escape "$short_hash")"
  printf '%s  "author": "%s",\n' "$indent" "$(json_escape "$author")"
  printf '%s  "authoredAt": "%s",\n' "$indent" "$(json_escape "$authored_at")"
  printf '%s  "subject": "%s",\n' "$indent" "$(json_escape "$subject")"
  printf '%s  "body": "%s"\n' "$indent" "$(json_escape "$body")"
  printf '%s}' "$indent"
}

json_escape() {
  local value="$1"
  value="${value//\\/\\\\}"
  value="${value//\"/\\\"}"
  value="${value//$'\n'/\\n}"
  value="${value//$'\r'/\\r}"
  value="${value//$'\t'/\\t}"
  printf '%s' "$value"
}

cmd_pack() {
  local output=""
  local mode="git"
  local git_spec=""
  local git_spec_provided=0
  local explicit_targets=0
  local targets=()
  local files=()

  while [ "$#" -gt 0 ]; do
    case "$1" in
      --backend)
        explicit_targets=1
        targets+=("backend")
        shift
        ;;
      --frontend)
        explicit_targets=1
        targets+=("frontend")
        shift
        ;;
      --target)
        explicit_targets=1
        case "${2:-}" in
          backend|frontend) targets+=("$2") ;;
          all) targets+=("backend" "frontend") ;;
          "") fail "--target requires backend, frontend, or all" ;;
          *) fail "unknown target: $2" ;;
        esac
        shift 2
        ;;
      --git)
        mode="git"
        if [ -n "${2:-}" ] && [[ "$2" != -* ]]; then
          git_spec="$2"
          git_spec_provided=1
          shift 2
        else
          shift
        fi
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
        mode="git"
        git_spec="$1"
        git_spec_provided=1
        shift
        ;;
    esac
  done

  if [ "${#targets[@]}" -eq 0 ]; then
    targets=("backend" "frontend")
  fi

  if [ -z "$output" ]; then
    output="$PROJECT_DIR/update-$(date -u +'%Y%m%d%H%M%S').zip"
  fi

  local file_list=""
  local source_file=""
  if [ "$mode" = "git" ]; then
    source_file="$(mktemp)"
    local target
    for target in "${targets[@]}"; do
      local spec="${git_spec:-HEAD}"
      local target_files=""
      if ! target_files="$(git_files_for_spec "$target" "$spec" 2>/tmp/musicdecrypto-pack-git-error.$$)"; then
        if [ "$explicit_targets" -eq 1 ] || [ "$git_spec_provided" -eq 0 ]; then
          cat /tmp/musicdecrypto-pack-git-error.$$ >&2 || true
          rm -f /tmp/musicdecrypto-pack-git-error.$$
          fail "failed to resolve git spec for $target: $spec"
        fi

        rm -f /tmp/musicdecrypto-pack-git-error.$$
        continue
      fi
      rm -f /tmp/musicdecrypto-pack-git-error.$$

      if [ -n "$target_files" ]; then
        file_list+="$(
          printf '%s\n' "$target_files" | normalize_target_files "$target"
        )"$'\n'
        printf '%s\t%s\n' "$target" "$spec" >>"$source_file"
      fi
    done
  else
    [ "${#targets[@]}" -eq 1 ] || fail "--files requires exactly one target; pass --backend or --frontend"
    file_list="$(manual_files "${targets[0]}" "${files[@]}")"
  fi

  file_list="$(printf '%s\n' "$file_list" | awk 'NF' | sort -u)"
  [ -n "$file_list" ] || fail "no files to package"

  local staging_dir
  staging_dir="$(mktemp -d)"
  trap "rm -rf '$staging_dir'" EXIT
  mkdir -p "$staging_dir/files"

  local target
  local path
  while IFS=$'\t' read -r target path; do
    local repo
    repo="$(repo_dir_for_target "$target")"
    mkdir -p "$staging_dir/files/$target/$(dirname "$path")"
    cp "$repo/$path" "$staging_dir/files/$target/$path"
  done <<<"$file_list"

  if [ "$mode" = "git" ]; then
    write_manifest "$staging_dir" "$source_file" <<<"$file_list"
    rm -f "$source_file"
  else
    write_manifest "$staging_dir" <<<"$file_list"
  fi

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
