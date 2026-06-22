#!/usr/bin/env bash
set -euo pipefail

configuration="${1:-Release}"
output="${2:-publish}"

dotnet restore
dotnet publish -c "$configuration" -o "$output"

echo "Published to $output"
