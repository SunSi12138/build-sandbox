#!/usr/bin/env bash

app_dir=$(CDPATH= cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P) || exit 1
cd -- "$app_dir" || exit 1

trap ':' INT
./StreetEcoTool-linux-arm64 "$@"
status=$?
trap - INT

printf '\n程序已退出（状态码 %s）。按回车关闭终端。\n' "$status"
if [[ -t 0 ]]; then IFS= read -r _; fi
exit "$status"
