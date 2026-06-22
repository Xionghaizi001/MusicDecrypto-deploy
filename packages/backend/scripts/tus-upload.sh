#!/usr/bin/env bash
set -euo pipefail

if [ "$#" -lt 2 ]; then
  echo "Usage: $0 <base-url> <file> [api-key]" >&2
  exit 1
fi

base_url="${1%/}"
file_path="$2"
api_key="${3:-}"

if [ ! -f "$file_path" ]; then
  echo "File not found: $file_path" >&2
  exit 1
fi

file_name="$(basename "$file_path")"
file_size="$(stat -c%s "$file_path")"
metadata="filename $(printf '%s' "$file_name" | base64 | tr -d '\n')"

headers=(
  -H "Tus-Resumable: 1.0.0"
  -H "Upload-Length: $file_size"
  -H "Upload-Metadata: $metadata"
)

if [ -n "$api_key" ]; then
  headers+=(-H "X-Api-Key: $api_key")
fi

upload_url="$(
  curl -fsS -D - -o /dev/null -X POST "${headers[@]}" "$base_url/files" \
    | awk 'BEGIN{IGNORECASE=1} /^Location:/ {gsub("\r", "", $2); print $2}'
)"

if [ -z "$upload_url" ]; then
  echo "Server did not return a tus Location header." >&2
  exit 1
fi

if [[ "$upload_url" != http* ]]; then
  upload_url="$base_url$upload_url"
fi

patch_headers=(
  -H "Tus-Resumable: 1.0.0"
  -H "Upload-Offset: 0"
  -H "Content-Type: application/offset+octet-stream"
)

if [ -n "$api_key" ]; then
  patch_headers+=(-H "X-Api-Key: $api_key")
fi

curl -fS -X PATCH "${patch_headers[@]}" --data-binary "@$file_path" "$upload_url"
echo
echo "Uploaded to $upload_url"
echo "Check jobs: curl ${api_key:+-H \"X-Api-Key: $api_key\" }$base_url/api/jobs"
