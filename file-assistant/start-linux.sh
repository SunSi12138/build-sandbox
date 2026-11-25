#!/usr/bin/env bash
set -e
app_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$app_dir"
exec ./FileAssistant "$@"
