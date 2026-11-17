#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""清理下载产物里的空白图/已知占位图。

用法:
  python clean_tiles.py --root 桑珠孜区_2025/imagery/18 --dry-run
  python clean_tiles.py --root 桑珠孜区_2025/imagery/18
"""
import argparse
import hashlib
from pathlib import Path

# 已知的接口占位图哈希：参数有误 / 流量上限 / 图源过期
BAD_HASHES = {
    "b95f278e783398edee1bfcccb9029f15",
    "131006d0572e3d71d67fba58b5fff1eb",
}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--root", required=True)
    ap.add_argument("--min-bytes", type=int, default=800)
    ap.add_argument("--dry-run", action="store_true")
    args = ap.parse_args()

    root = Path(args.root)
    small = 0
    bad = 0
    kept = 0
    for p in root.rglob("*.webp"):
        try:
            size = p.stat().st_size
            digest = hashlib.md5(p.read_bytes()).hexdigest()
        except OSError:
            continue
        if size < args.min_bytes:
            small += 1
            if not args.dry_run:
                p.unlink()
        elif digest in BAD_HASHES:
            bad += 1
            if not args.dry_run:
                p.unlink()
        else:
            kept += 1
    action = "将删除" if args.dry_run else "已删除"
    print(f"{action} 空白图 {small:,}，占位图 {bad:,}；保留 {kept:,}")


if __name__ == "__main__":
    raise SystemExit(main())
