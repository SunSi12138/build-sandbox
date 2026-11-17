#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""生成 z18 缺失瓦片列表，可按市中心距离排序并切分给多个 jl1_tile 进程。

用法:
  python prepare_z18_missing.py --aoi aoi.geojson --out 桑珠孜区_2025 --list missing_z18.txt
  python prepare_z18_missing.py --aoi aoi.geojson --out 桑珠孜区_2025 --list missing_z18.txt --shards 4
"""
import argparse
import json
import math
from pathlib import Path

from shapely.geometry import box, shape
from shapely.prepared import prep

Z = 18
CENTER = (88.88, 29.27)


def tile_bounds(x, y):
    n = 1 << Z
    lon1 = x / n * 360 - 180
    lon2 = (x + 1) / n * 360 - 180
    lat1 = math.degrees(math.atan(math.sinh(math.pi * (1 - 2 * (y + 1) / n))))
    lat2 = math.degrees(math.atan(math.sinh(math.pi * (1 - 2 * y / n))))
    return box(lon1, lat1, lon2, lat2)


def tile_center(x, y):
    n = 1 << Z
    lon = (x + 0.5) / n * 360 - 180
    lat = math.degrees(math.atan(math.sinh(math.pi * (1 - 2 * (y + 0.5) / n))))
    return lon, lat


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--aoi", required=True)
    ap.add_argument("--out", required=True, help="数据目录，瓦片在 <out>/imagery/18/x/y.webp")
    ap.add_argument("--list", default="missing_z18.txt")
    ap.add_argument("--shards", type=int, default=1)
    args = ap.parse_args()

    aoi = shape(json.loads(Path(args.aoi).read_text(encoding="utf-8")))
    prepared = prep(aoi)
    n = 1 << Z
    minx, miny, maxx, maxy = aoi.bounds
    x0 = int((minx + 180) / 360 * n)
    x1 = int((maxx + 180) / 360 * n)

    def lat2y(lat):
        r = math.radians(lat)
        return int((1 - math.log(math.tan(r) + 1 / math.cos(r)) / math.pi) / 2 * n)

    y0 = lat2y(maxy)
    y1 = lat2y(miny)
    base = Path(args.out) / "imagery" / str(Z)
    missing = []
    total = 0
    for x in range(x0, x1 + 1):
        for y in range(y0, y1 + 1):
            if not prepared.intersects(tile_bounds(x, y)):
                continue
            total += 1
            p = base / str(x) / f"{y}.webp"
            if not (p.exists() and p.stat().st_size > 800):
                missing.append((x, y))

    missing.sort(key=lambda t: (tile_center(*t)[0] - CENTER[0]) ** 2 + (tile_center(*t)[1] - CENTER[1]) ** 2)
    list_path = Path(args.list)
    rows = [f"{x} {y}" for x, y in missing]
    if args.shards <= 1:
        out_files = [list_path]
    else:
        out_files = [
            list_path.with_name(f"{list_path.stem}_shard{i+1}{list_path.suffix}")
            for i in range(args.shards)
        ]
    if not rows:
        print("没有缺失瓦片")
        return 0
    for i, path in enumerate(out_files):
        chunk = rows[i * len(rows) // len(out_files):(i + 1) * len(rows) // len(out_files)]
        path.write_text("\n".join(chunk), encoding="utf-8")
        print(f"{path}: {len(chunk):,}")
    print(f"区域 z18 网格: {total:,}，缺失: {len(missing):,}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
