#!/usr/bin/env bash

# 获取脚本所在目录
app_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)" || exit 1

# 切换到程序目录
cd "$app_dir" || exit 1

# 启动主程序
./StreetEcoTool-linux-arm64 "$@"