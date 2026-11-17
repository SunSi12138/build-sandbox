#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
吉林一号网（jl1mall.com）“实验室”切片影像下载器
=================================================

用途
----
按 GeoJSON 边界，从吉林一号实验室的 XYZ 切片服务下载：
  * 卫星影像（例如“2025年全国高质量一张图”，0.5 m）
  * 地名/道路标注（页面所用的天地图 cva_w 透明注记层）

特点
----
  * 先出额度计划（plan），确认够不够再下载；
  * 瓦片按 TMS 规则请求，自动计算 sign；
  * 断点续传：已存在的瓦片自动跳过；
  * 按“最高清优先”下载，可设置每日额度上限；
  * 影像与标注分开保存，另可生成低分辨率预览图；
  * 输出 GeoJSON、metadata.json、quota_plan.txt，便于复核和复用。

依赖
----
  pip install -r requirements.txt
  （requests、Pillow 必需；shapely 强烈建议，用于精确统计边界瓦片）

示例
----
  set JL1_TOKEN=你的32位token
  python jl1mall_crawler.py info
  python jl1mall_crawler.py plan --aoi aoi_sangzhuzi.geojson ^
      --detail-aoi core_sangzhuzi.geojson --detail-zoom 18 --zooms 10-18
  python jl1mall_crawler.py download --aoi aoi_sangzhuzi.geojson ^
      --detail-aoi core_sangzhuzi.geojson --detail-zoom 18 --zooms 10-18 ^
      --layers both --out .\桑珠孜区_2025 --workers 16 --daily-budget 39000
  python jl1mall_crawler.py stitch --out .\桑珠孜区_2025 --zooms 10-18

注意
----
  * token 从浏览器 localStorage 的 "token" 读取；不要提交到代码仓库。
  * 吉林一号免费流量为每日 40,000 次，次日重置；本工具按 0.5 次/瓦片估算，
    实际以平台计费为准。下载前请先用 plan 核对。
  * 天地图注记为第三方免费服务，请遵守其服务条款；影像请遵守吉林一号授权。
"""

from __future__ import annotations

import argparse
import base64
import concurrent.futures as cf
import dataclasses
import hashlib
import io
import json
import math
import os
import queue
import random
import re
import shutil
import sqlite3
import sys
import threading
import time
import urllib.parse
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Dict, Iterable, Iterator, List, Optional, Sequence, Tuple

# Windows 控制台默认可能是 GBK；统一用 UTF-8，避免中文日志乱码。
for _stream in (sys.stdout, sys.stderr):
    try:
        _stream.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass

try:
    import requests
    from requests.adapters import HTTPAdapter
    from urllib3.util.retry import Retry
except Exception:  # pragma: no cover
    requests = None

try:
    from PIL import Image, ImageDraw
except Exception:  # pragma: no cover
    Image = None
    ImageDraw = None

try:
    from shapely.geometry import box as shapely_box, shape as shapely_shape
    from shapely.geometry import MultiPolygon, Polygon
    from shapely.ops import unary_union
    from shapely.prepared import prep
    HAS_SHAPELY = True
except Exception:  # pragma: no cover
    HAS_SHAPELY = False
    shapely_box = None
    shapely_shape = None
    MultiPolygon = None
    Polygon = None
    unary_union = None
    prep = None

API_BASE = "https://www.jl1mall.com"
TILE_BASE = "https://api.jl1mall.com"
DEFAULT_MAP = "2025年全国高质量一张图"
DEFAULT_LABEL_URL = "https://t{s}.tianditu.gov.cn/DataServer?T=cva_w&x={x}&y={y}&l={z}&tk={tk}"
DEFAULT_LABEL_TK = "a1acdff888c05743e3603302c3f64a6d"
EARTH_R = 6371008.8


# ---------------------------------------------------------------------------
# 基础工具
# ---------------------------------------------------------------------------

def eprint(*args: Any, **kwargs: Any) -> None:
    kwargs.setdefault("file", sys.stderr)
    kwargs.setdefault("flush", True)
    print(*args, **kwargs)


def log(msg: str) -> None:
    eprint(f"[{datetime.now().strftime('%H:%M:%S')}] {msg}")


def human_size(n: float) -> str:
    for unit in ("B", "KB", "MB", "GB", "TB"):
        if abs(n) < 1024 or unit == "TB":
            return f"{n:.1f} {unit}"
        n /= 1024
    return f"{n:.1f} TB"


def ceil_div(a: float, b: float) -> int:
    if b <= 0:
        return 0
    return int(math.ceil(a / b))


def chunks(seq: Sequence[Any], size: int) -> Iterator[Sequence[Any]]:
    for i in range(0, len(seq), size):
        yield seq[i:i + size]


def parse_zooms(spec: str) -> List[int]:
    out: List[int] = []
    for part in spec.split(","):
        part = part.strip()
        if not part:
            continue
        if "-" in part:
            a, b = part.split("-", 1)
            out.extend(range(int(a), int(b) + 1))
        else:
            out.append(int(part))
    out = sorted(set(z for z in out if 0 <= z <= 22))
    if not out:
        raise ValueError("zooms 为空")
    return out


def parse_point(spec: Optional[str]) -> Optional[Tuple[float, float]]:
    if not spec:
        return None
    a, b = spec.split(",", 1)
    return float(a), float(b)


def mask_account(s: str) -> str:
    if not s:
        return s
    if len(s) <= 4:
        return "*" * len(s)
    return s[:2] + "*" * (len(s) - 4) + s[-2:]


# ---------------------------------------------------------------------------
# API 客户端
# ---------------------------------------------------------------------------

class CrawlerError(RuntimeError):
    pass


class AuthError(CrawlerError):
    pass


class QuotaError(CrawlerError):
    pass


class ApiError(CrawlerError):
    def __init__(self, message: str, code: Any = None, payload: Any = None):
        super().__init__(message)
        self.code = code
        self.payload = payload


class Jl1Client:
    def __init__(
        self,
        token: str,
        timeout: float = 40.0,
        proxy: Optional[str] = None,
        user_agent: Optional[str] = None,
    ):
        if requests is None:
            raise CrawlerError("缺少 requests，请先执行: pip install requests")
        self.token = token.strip().removeprefix("Bearer ").strip()
        self.timeout = timeout
        self.base = API_BASE
        self.session = requests.Session()
        self.session.headers.update({
            "Authorization": "Bearer " + self.token,
            "Accept": "application/json, text/plain, */*",
            "User-Agent": user_agent or (
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) "
                "AppleWebKit/537.36 (KHTML, like Gecko) "
                "Chrome/157.0.0.0 Safari/537.36 Edg/157.0.0.0"
            ),
        })
        if proxy:
            self.session.proxies.update({"http": proxy, "https": proxy})
        self.map_info: Optional[Dict[str, Any]] = None
        self.tk: Optional[str] = None
        self.sk: Optional[str] = None

    def _request(self, method: str, path: str, **kwargs: Any) -> Any:
        url = self.base + path
        try:
            r = self.session.request(method, url, timeout=self.timeout, **kwargs)
        except Exception as exc:
            raise CrawlerError(f"请求失败: {path}: {exc}") from exc
        if r.status_code in (401, 403):
            raise AuthError(f"登录失效或无权访问 ({r.status_code})，请重新获取 token")
        try:
            payload = r.json()
        except Exception:
            raise ApiError(f"非 JSON 响应: {path} HTTP {r.status_code}: {r.text[:300]}")
        code = payload.get("code", payload.get("status"))
        if code not in (200, "200", 0, "0", True):
            msg = payload.get("msg") or payload.get("message") or payload
            raise ApiError(f"{path} 失败: {msg}", code=code, payload=payload)
        return payload.get("data")

    def login_info(self) -> Dict[str, Any]:
        return self._request("GET", "/loginapi/getLoginInfo") or {}

    def series(self, kind: str = "basic") -> List[Dict[str, Any]]:
        data = self._request(
            "GET",
            "/tileflowapi/imagery/mk/series",
            params={"type": kind, "callScenario": "jl1"},
        )
        return data or []

    def find_map(self, name: str = DEFAULT_MAP, kind: str = "basic") -> Dict[str, Any]:
        leaves: List[Dict[str, Any]] = []

        def walk(nodes: Any) -> None:
            if not isinstance(nodes, list):
                return
            for node in nodes:
                if not isinstance(node, dict):
                    continue
                val = node.get("value")
                if isinstance(val, list):
                    walk(val)
                elif node.get("iv"):
                    leaves.append(node)

        walk(self.series(kind))
        normalized = name.replace(" ", "").strip()
        exact = [x for x in leaves if (x.get("name") or "").replace(" ", "").strip() == normalized]
        if not exact:
            exact = [x for x in leaves if normalized in (x.get("name") or "").replace(" ", "")]
        if not exact:
            names = sorted(set(x.get("name", "") for x in leaves if x.get("name")))
            raise CrawlerError("找不到图源: %s\n可用图源:\n  %s" % (name, "\n  ".join(names)))
        exact.sort(key=lambda x: (0 if x.get("mkType") == 4 else 1, x.get("name") != name))
        self.map_info = exact[0]
        return self.map_info

    def ensure_map(self, name: str = DEFAULT_MAP) -> Dict[str, Any]:
        if not self.map_info:
            self.find_map(name)
        return self.map_info

    def user_tk(self) -> Tuple[str, str]:
        data = self._request(
            "GET", "/tileflowapi/imagery/getUserTk", params={"callScenario": "jl1"}
        ) or {}
        self.tk = data.get("tk")
        self.sk = data.get("sk")
        if not self.tk or not self.sk:
            raise CrawlerError("getUserTk 未返回 tk/sk")
        return self.tk, self.sk

    def ensure_tk(self) -> None:
        if not self.tk or not self.sk:
            self.user_tk()

    def quota(self) -> Dict[str, Any]:
        info = self.ensure_map()
        mk_type = info.get("mkType", 4)
        data = self._request(
            "GET",
            "/tileflowapi/imagery/traffic/situation",
            params={"packageType": "basic", "mkType": str(mk_type)},
        ) or {}
        return data

    def image_url(self, z: int, x: int, y_xyz: int) -> str:
        self.ensure_map()
        self.ensure_tk()
        ty = (1 << z) - 1 - y_xyz
        path = (
            f"/getTile/{z}/{x}/{ty}?mk={urllib.parse.quote(str(self.map_info.get('iv')))}"
            f"&tk={urllib.parse.quote(str(self.tk))}&vf=0"
        )
        sign = hashlib.md5((path + str(self.sk)).encode("utf-8")).hexdigest()
        return TILE_BASE + path + "&sign=" + sign

    def label_url(self, z: int, x: int, y_xyz: int, template: str, label_tk: str) -> str:
        s = str((x + y_xyz) % 8)
        return template.format(s=s, z=z, x=x, y=y_xyz, tk=label_tk)


# ---------------------------------------------------------------------------
# GeoJSON / 瓦片几何
# ---------------------------------------------------------------------------

def load_geojson(source: str) -> Dict[str, Any]:
    text = source.strip()
    if text.startswith("{"):
        return json.loads(text)
    path = Path(source)
    if not path.exists():
        raise CrawlerError(f"GeoJSON 不存在: {source}")
    return json.loads(path.read_text(encoding="utf-8"))


def _as_polygonal(geom: Any) -> Any:
    if HAS_SHAPELY:
        if geom.is_empty:
            raise CrawlerError("GeoJSON 为空")
        if not geom.is_valid:
            geom = geom.buffer(0)
        if geom.geom_type in ("Polygon", "MultiPolygon"):
            return geom
        if geom.geom_type == "GeometryCollection":
            polys = [g for g in geom.geoms if g.geom_type in ("Polygon", "MultiPolygon")]
            if not polys:
                raise CrawlerError("GeoJSON 中没有面")
            return unary_union(polys)
        raise CrawlerError(f"不支持的几何类型: {geom.geom_type}")
    raise CrawlerError("需要 shapely 才能解析 GeoJSON")


def geojson_to_geometry(obj: Dict[str, Any]) -> Any:
    t = obj.get("type")
    if t == "FeatureCollection":
        geoms = [geojson_to_geometry(f) for f in obj.get("features", [])]
        if HAS_SHAPELY:
            return _as_polygonal(unary_union(geoms))
        raise CrawlerError("需要 shapely")
    if t == "Feature":
        return geojson_to_geometry(obj.get("geometry") or {})
    if HAS_SHAPELY:
        return _as_polygonal(shapely_shape(obj))
    raise CrawlerError("需要 shapely 才能解析 GeoJSON")


def ring_area_km2(coords: Sequence[Sequence[float]]) -> float:
    total = 0.0
    for (lon1, lat1), (lon2, lat2) in zip(coords, coords[1:]):
        total += math.radians(lon2 - lon1) * (
            2.0 + math.sin(math.radians(lat1)) + math.sin(math.radians(lat2))
        )
    return abs(total) * EARTH_R * EARTH_R / 2.0 / 1_000_000.0


def geodesic_area_km2(geom: Any) -> float:
    polys = list(geom.geoms) if geom.geom_type == "MultiPolygon" else [geom]
    area = 0.0
    for poly in polys:
        area += ring_area_km2(list(poly.exterior.coords))
        for interior in poly.interiors:
            area -= ring_area_km2(list(interior.coords))
    return max(area, 0.0)


def tile_lonlat_bounds(z: int, x: int, y_xyz: int) -> Tuple[float, float, float, float]:
    n = 1 << z
    lon1 = x / n * 360.0 - 180.0
    lon2 = (x + 1) / n * 360.0 - 180.0
    lat1 = math.degrees(math.atan(math.sinh(math.pi * (1.0 - 2.0 * (y_xyz + 1) / n))))
    lat2 = math.degrees(math.atan(math.sinh(math.pi * (1.0 - 2.0 * y_xyz / n))))
    return lon1, lat1, lon2, lat2


def tile_center_lonlat(z: int, x: int, y_xyz: int) -> Tuple[float, float]:
    lon1, lat1, lon2, lat2 = tile_lonlat_bounds(z, x, y_xyz)
    return (lon1 + lon2) / 2.0, (lat1 + lat2) / 2.0


def _point_in_ring(lon: float, lat: float, ring: Sequence[Sequence[float]]) -> bool:
    inside = False
    n = len(ring)
    j = n - 1
    for i in range(n):
        xi, yi = ring[i][0], ring[i][1]
        xj, yj = ring[j][0], ring[j][1]
        if ((yi > lat) != (yj > lat)) and (
            lon < (xj - xi) * (lat - yi) / (yj - yi + 1e-30) + xi
        ):
            inside = not inside
        j = i
    return inside


def _point_in_polygon(lon: float, lat: float, poly: Any) -> bool:
    if not _point_in_ring(lon, lat, list(poly.exterior.coords)):
        return False
    for interior in poly.interiors:
        if _point_in_ring(lon, lat, list(interior.coords)):
            return False
    return True


def _segments_intersect(
    a: Tuple[float, float], b: Tuple[float, float],
    c: Tuple[float, float], d: Tuple[float, float],
) -> bool:
    def orient(p: Tuple[float, float], q: Tuple[float, float], r: Tuple[float, float]) -> float:
        return (q[0] - p[0]) * (r[1] - p[1]) - (q[1] - p[1]) * (r[0] - p[0])

    def on_seg(p: Tuple[float, float], q: Tuple[float, float], r: Tuple[float, float]) -> bool:
        return (
            min(p[0], r[0]) - 1e-12 <= q[0] <= max(p[0], r[0]) + 1e-12
            and min(p[1], r[1]) - 1e-12 <= q[1] <= max(p[1], r[1]) + 1e-12
        )

    o1, o2, o3, o4 = orient(a, b, c), orient(a, b, d), orient(c, d, a), orient(c, d, b)
    if (o1 > 0) != (o2 > 0) and (o3 > 0) != (o4 > 0):
        return True
    if abs(o1) < 1e-12 and on_seg(a, c, b):
        return True
    if abs(o2) < 1e-12 and on_seg(a, d, b):
        return True
    if abs(o3) < 1e-12 and on_seg(c, a, d):
        return True
    if abs(o4) < 1e-12 and on_seg(c, b, d):
        return True
    return False


class AOI:
    def __init__(self, geom: Any, name: str = "AOI"):
        self.geom = geom
        self.name = name
        self.bounds = tuple(geom.bounds)
        self.polygons = list(geom.geoms) if geom.geom_type == "MultiPolygon" else [geom]
        self.prepared = prep(geom) if HAS_SHAPELY else None
        self.area_km2 = geodesic_area_km2(geom)

    def intersects_tile(self, z: int, x: int, y_xyz: int) -> bool:
        lon1, lat1, lon2, lat2 = tile_lonlat_bounds(z, x, y_xyz)
        if self.prepared is not None:
            return bool(self.prepared.intersects(shapely_box(lon1, lat1, lon2, lat2)))
        # 无 shapely 时的保守判断
        corners = [(lon1, lat1), (lon2, lat1), (lon1, lat2), (lon2, lat2)]
        for lon, lat in corners:
            if any(_point_in_polygon(lon, lat, p) for p in self.polygons):
                return True
        rect = [(lon1, lat1), (lon2, lat1), (lon2, lat2), (lon1, lat2)]
        for poly in self.polygons:
            for ring in [poly.exterior] + list(poly.interiors):
                coords = list(ring.coords)
                for lon, lat in coords:
                    if lon1 <= lon <= lon2 and lat1 <= lat <= lat2:
                        return True
                for a, b in zip(coords, coords[1:]):
                    for c, d in zip(rect, rect[1:] + rect[:1]):
                        if _segments_intersect(a, b, c, d):
                            return True
        return False

    def tile_count(self, z: int, verbose: bool = False) -> int:
        minx, miny, maxx, maxy = self.bounds
        n = 1 << z
        x0 = max(0, int((minx + 180.0) / 360.0 * n))
        x1 = min(n - 1, int((maxx + 180.0) / 360.0 * n))

        def lat2y(lat: float) -> int:
            r = math.radians(max(min(lat, 85.05112878), -85.05112878))
            return int((1.0 - math.log(math.tan(r) + 1.0 / math.cos(r)) / math.pi) / 2.0 * n)

        y0 = max(0, lat2y(maxy))
        y1 = min(n - 1, lat2y(miny))
        cnt = 0
        for x in range(x0, x1 + 1):
            for y in range(y0, y1 + 1):
                if self.intersects_tile(z, x, y):
                    cnt += 1
            if verbose and (x - x0) % 50 == 0:
                eprint(f"\r  z{z}: 扫描 {x - x0 + 1}/{x1 - x0 + 1} 列, 已命中 {cnt}", end="")
        if verbose:
            eprint()
        return cnt

    def iter_tiles(self, z: int) -> Iterator[Tuple[int, int, int]]:
        minx, miny, maxx, maxy = self.bounds
        n = 1 << z
        x0 = max(0, int((minx + 180.0) / 360.0 * n))
        x1 = min(n - 1, int((maxx + 180.0) / 360.0 * n))

        def lat2y(lat: float) -> int:
            r = math.radians(max(min(lat, 85.05112878), -85.05112878))
            return int((1.0 - math.log(math.tan(r) + 1.0 / math.cos(r)) / math.pi) / 2.0 * n)

        y0 = max(0, lat2y(maxy))
        y1 = min(n - 1, lat2y(miny))
        for x in range(x0, x1 + 1):
            for y in range(y0, y1 + 1):
                if self.intersects_tile(z, x, y):
                    yield z, x, y

    def centroid(self) -> Tuple[float, float]:
        if HAS_SHAPELY:
            c = self.geom.centroid
            return c.x, c.y
        minx, miny, maxx, maxy = self.bounds
        return (minx + maxx) / 2.0, (miny + maxy) / 2.0


def save_geojson(geom: Any, path: Path) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    if HAS_SHAPELY:
        from shapely.geometry import mapping
        obj = mapping(geom)
    else:
        obj = {"type": "Polygon", "coordinates": []}
    path.write_text(json.dumps(obj, ensure_ascii=False, indent=2), encoding="utf-8")


# ---------------------------------------------------------------------------
# 计划
# ---------------------------------------------------------------------------

@dataclasses.dataclass
class PlanRow:
    zoom: int
    coverage: str
    tiles: int
    cost: float
    area_km2: float

    def as_dict(self) -> Dict[str, Any]:
        return dataclasses.asdict(self)


def build_zoom_specs(
    zooms: Sequence[int],
    aoi: AOI,
    detail_aoi: Optional[AOI],
    detail_zoom: Optional[int],
) -> List[Tuple[int, AOI, str]]:
    out = []
    for z in zooms:
        if detail_aoi is not None and detail_zoom is not None and z == detail_zoom:
            out.append((z, detail_aoi, "核心区"))
        else:
            out.append((z, aoi, "全区"))
    return out


def print_table(rows: Sequence[PlanRow], cost_per_tile: float, avg_kb: float) -> None:
    print("")
    print(f"{'缩放':>4}  {'范围':<6}  {'瓦片数':>10}  {'预计流量':>10}  {'预计存储':>10}")
    print("-" * 62)
    for r in rows:
        print(
            f"{r.zoom:>4}  {r.coverage:<6}  {r.tiles:>10,}  "
            f"{r.cost:>10,.1f}  {r.tiles * avg_kb / 1024:>8,.1f} MB"
        )
    print("-" * 62)
    total_tiles = sum(r.tiles for r in rows)
    total_cost = sum(r.cost for r in rows)
    print(
        f"{'合计':>4}  {'':<6}  {total_tiles:>10,}  {total_cost:>10,.1f}  "
        f"{total_tiles * avg_kb / 1024:>8,.1f} MB"
    )


def cmd_plan(args: argparse.Namespace) -> int:
    client = make_client(args)
    user = client.login_info()
    info = client.find_map(args.map_name)
    tk, sk = client.user_tk()
    q = client.quota()

    aoi = AOI(geojson_to_geometry(load_geojson(args.aoi)), "主区域")
    detail = None
    if args.detail_aoi:
        detail = AOI(geojson_to_geometry(load_geojson(args.detail_aoi)), "核心区")

    zooms = parse_zooms(args.zooms)
    specs = build_zoom_specs(zooms, aoi, detail, args.detail_zoom)
    rows: List[PlanRow] = []
    for z, a, coverage in specs:
        log(f"统计 z{z} {coverage} 瓦片数 ...")
        cnt = a.tile_count(z)
        rows.append(PlanRow(z, coverage, cnt, cnt * args.cost_per_tile, a.area_km2))
        log(f"z{z}: {cnt:,} 瓦片, 流量 {cnt * args.cost_per_tile:,.1f}")

    flow_sum = float(q.get("flowSum") or 0)
    used = float(q.get("usedCount") or 0)
    remaining = max(0.0, flow_sum - used)
    total_tiles = sum(r.tiles for r in rows)
    total_cost = sum(r.cost for r in rows)

    # 若只把最高层限制在核心区，额外算一下“最高层全区”的代价，方便比较。
    full_top_tiles = None
    if detail is not None and args.detail_zoom in zooms:
        log(f"统计 z{args.detail_zoom} 全区瓦片数（用于对比）...")
        full_top_tiles = aoi.tile_count(args.detail_zoom)


    print("")
    print("=" * 70)
    print("吉林一号切片下载额度计划")
    print("=" * 70)
    print(f"账号: {mask_account(str(user.get('account') or user.get('name') or ''))}")
    print(f"图源: {info.get('name')}  (分辨率 {info.get('resolution')} m, mkType={info.get('mkType')})")
    print(f"审图号: GS(2026)4301号（页面显示，供参考）")
    print(f"主区域: {aoi.name}, 面积约 {aoi.area_km2:,.1f} km², 边界 {tuple(round(x, 4) for x in aoi.bounds)}")
    if detail:
        print(f"核心区: 面积约 {detail.area_km2:,.1f} km², 边界 {tuple(round(x, 4) for x in detail.bounds)}")
    print(f"当前流量: {q.get('flowType')} {used:,.1f}/{flow_sum:,.0f} 次，剩余 {remaining:,.1f} 次")
    print(f"计费假设: {args.cost_per_tile} 次/瓦片；免费流量每日 40,000 次，次日重置")
    if not HAS_SHAPELY:
        print("警告: 未安装 shapely，边界瓦片数为保守估算，可能略多。建议 pip install shapely")
    print_table(rows, args.cost_per_tile, args.avg_tile_kb)

    if full_top_tiles is not None:
        top = args.detail_zoom
        top_cost = full_top_tiles * args.cost_per_tile
        verdict = "够" if top_cost <= remaining else f"不够，需约 {ceil_div(top_cost, flow_sum or 40000)} 天"
        print(
            f"\n对比: z{top} 全区 = {full_top_tiles:,} 瓦片, "
            f"{top_cost:,.1f} 次（{verdict}）；本计划中 z{top} 只取核心区。"
        )

    print("")
    print("结论:")
    if total_cost <= remaining:
        print(f"  [够] 一次可下载完：需 {total_cost:,.1f} 次，剩余 {remaining:,.1f} 次。")
    else:
        days = ceil_div(total_cost, flow_sum if flow_sum > 0 else 40000)
        print(f"  [不够一次] 需 {total_cost:,.1f} 次，当前剩余 {remaining:,.1f} 次，缺 {total_cost - remaining:,.1f} 次。")
        print(f"  按每日 {flow_sum:,.0f} 次、次日重置计算，约需 {days} 天，可断点续传。")

    # 推荐：z17 全区 + z18 核心
    rec_rows = [r for r in rows if r.zoom <= 17]
    rec_cost = sum(r.cost for r in rec_rows)
    if detail is not None and 18 in zooms:
        top_core = next((r.cost for r in rows if r.zoom == 18 and r.coverage == "核心区"), 0.0)
        rec_cost += top_core
    if rec_cost > 0:
        print(f"  推荐一日方案: z17 及以下全区 + z18 核心区，预计 {rec_cost:,.1f} 次。")
        if rec_cost <= remaining:
            print(f"    当前额度可覆盖（剩余 {remaining - rec_cost:,.1f} 次）。")
        else:
            print(f"    仍缺 {rec_cost - remaining:,.1f} 次，可减少核心区范围或分两天。")

    out = Path(args.out)
    out.mkdir(parents=True, exist_ok=True)
    save_geojson(aoi.geom, out / "aoi.geojson")
    if detail:
        save_geojson(detail.geom, out / "detail_aoi.geojson")
    plan_obj = {
        "generated_at": datetime.now(timezone.utc).isoformat(),
        "account": mask_account(str(user.get("account") or "")),
        "map": {
            "name": info.get("name"),
            "iv": info.get("iv"),
            "mkType": info.get("mkType"),
            "resolution": info.get("resolution"),
        },
        "quota": q,
        "cost_per_tile": args.cost_per_tile,
        "zooms": zooms,
        "detail_zoom": args.detail_zoom,
        "rows": [r.as_dict() for r in rows],
        "total_tiles": total_tiles,
        "total_cost": total_cost,
        "remaining": remaining,
    }
    (out / "quota_plan.json").write_text(
        json.dumps(plan_obj, ensure_ascii=False, indent=2), encoding="utf-8"
    )
    lines = [
        "吉林一号切片下载额度计划",
        f"图源: {info.get('name')} ({info.get('resolution')} m)",
        f"主区域面积: {aoi.area_km2:.1f} km2",
        f"剩余流量: {remaining:.1f} / {flow_sum:.0f} 次",
        f"计费: {args.cost_per_tile} 次/瓦片",
        "",
        "zoom\tcoverage\ttiles\tcost",
    ]
    lines += [f"{r.zoom}\t{r.coverage}\t{r.tiles}\t{r.cost:.1f}" for r in rows]
    lines += [f"TOTAL\t-\t{total_tiles}\t{total_cost:.1f}"]
    (out / "quota_plan.txt").write_text("\n".join(lines) + "\n", encoding="utf-8")
    print(f"\n计划已写入: {out / 'quota_plan.txt'}")
    return 0


# ---------------------------------------------------------------------------
# 下载
# ---------------------------------------------------------------------------

_thread_local = threading.local()


def make_client(args: argparse.Namespace) -> Jl1Client:
    token = resolve_token(args)
    return Jl1Client(token, timeout=args.timeout, proxy=args.proxy)


def resolve_token(args: argparse.Namespace) -> str:
    token = getattr(args, "token", None) or os.environ.get("JL1_TOKEN", "")
    if not token and getattr(args, "token_file", None):
        token = Path(args.token_file).read_text(encoding="utf-8").strip()
    token = (token or "").strip().removeprefix("Bearer ").strip()
    if not token:
        raise CrawlerError(
            "未提供 token。请从浏览器 localStorage 读取 token，然后：\n"
            "  set JL1_TOKEN=你的token\n"
            "或  --token 你的token"
        )
    return token


def get_thread_session(args: argparse.Namespace) -> requests.Session:
    s = getattr(_thread_local, "session", None)
    if s is None:
        s = requests.Session()
        s.headers.update({
            "Accept": "image/webp,image/png,image/*,*/*;q=0.8",
            "User-Agent": (
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) "
                "AppleWebKit/537.36 (KHTML, like Gecko) "
                "Chrome/157.0.0.0 Safari/537.36 Edg/157.0.0.0"
            ),
            "Referer": "https://www.jl1mall.com/lab/",
            "Origin": "https://www.jl1mall.com",
        })
        retry = Retry(
            total=2,
            connect=2,
            read=2,
            backoff_factor=0.4,
            status_forcelist=(429, 500, 502, 503, 504),
            allowed_methods=frozenset(["GET"]),
            raise_on_status=False,
        )
        adapter = HTTPAdapter(
            max_retries=retry,
            pool_connections=max(8, args.workers * 2),
            pool_maxsize=max(8, args.workers * 2),
        )
        s.mount("https://", adapter)
        s.mount("http://", adapter)
        if args.proxy:
            s.proxies.update({"http": args.proxy, "https": args.proxy})
        _thread_local.session = s
    return s


def is_blank_label(data: bytes) -> bool:
    if len(data) < 400:
        return True
    if Image is None:
        return False
    try:
        im = Image.open(io.BytesIO(data))
        if im.mode not in ("RGBA", "LA"):
            im = im.convert("RGBA")
        alpha = im.getchannel("A")
        return alpha.getbbox() is None
    except Exception:
        return False


def tile_path(out: Path, kind: str, z: int, x: int, y: int, ext: str) -> Path:
    return out / kind / str(z) / str(x) / f"{y}.{ext}"


def existing_tile(out: Path, kind: str, z: int, x: int, y: int) -> bool:
    default_ext = "webp" if kind == "imagery" else "png"
    for ext in (default_ext, "png", "jpg", "jpeg", "webp"):
        p = tile_path(out, kind, z, x, y, ext)
        if p.exists() and p.stat().st_size > 0:
            return True
    return False


def download_tile(
    client: Jl1Client,
    args: argparse.Namespace,
    out: Path,
    kind: str,
    tile: Tuple[int, int, int],
) -> Dict[str, Any]:
    z, x, y = tile
    if kind == "imagery":
        url = client.image_url(z, x, y)
        default_ext = "webp"
    else:
        url = client.label_url(z, x, y, args.label_url, args.label_tk)
        default_ext = "png"

    # 先探测已有文件（扩展名可能不同）
    if not getattr(args, "overwrite", False) and existing_tile(out, kind, z, x, y):
        return {"status": "skip", "bytes": 0, "tile": tile}

    session = get_thread_session(args)
    last_err = ""
    for attempt in range(max(1, args.retries)):
        try:
            r = session.get(url, timeout=(10, args.timeout))
            if r.status_code in (401, 403):
                raise AuthError(f"HTTP {r.status_code}，token 可能失效")
            if r.status_code == 404:
                return {"status": "fail", "bytes": 0, "tile": tile, "error": "404"}
            if r.status_code != 200:
                last_err = f"HTTP {r.status_code}"
                if r.status_code == 429:
                    time.sleep(1.5 * (attempt + 1))
                    continue
                time.sleep(0.3 * (attempt + 1))
                continue
            data = r.content
            ctype = (r.headers.get("content-type") or "").lower()
            if not data:
                last_err = "空响应"
                continue
            if not ctype.startswith("image/"):
                # 可能是额度耗尽/错误 JSON
                text = data[:500].decode("utf-8", "ignore")
                if "flow" in text.lower() or "额度" in text or "流量" in text:
                    raise QuotaError(f"流量不足: {text}")
                last_err = f"非图片: {ctype} {text[:120]}"
                continue
            if kind == "labels" and args.label_skip_blank and is_blank_label(data):
                return {"status": "blank", "bytes": 0, "tile": tile}
            ext = default_ext
            if "jpeg" in ctype or "jpg" in ctype:
                ext = "jpg"
            elif "png" in ctype:
                ext = "png"
            elif "webp" in ctype:
                ext = "webp"
            p = tile_path(out, kind, z, x, y, ext)
            p.parent.mkdir(parents=True, exist_ok=True)
            tmp = p.with_suffix(p.suffix + ".part")
            tmp.write_bytes(data)
            os.replace(tmp, p)
            return {"status": "ok", "bytes": len(data), "tile": tile}
        except (AuthError, QuotaError):
            raise
        except Exception as exc:
            last_err = str(exc)
            time.sleep(0.3 * (attempt + 1))
    return {"status": "fail", "bytes": 0, "tile": tile, "error": last_err}


def download_batch(
    client: Jl1Client,
    args: argparse.Namespace,
    out: Path,
    kind: str,
    tiles: Sequence[Tuple[int, int, int]],
    budget_left: float,
) -> Dict[str, Any]:
    stats = {"ok": 0, "skip": 0, "blank": 0, "fail": 0, "bytes": 0, "stopped": None}
    if not tiles:
        return stats

    total = len(tiles)
    start = time.time()
    stop = threading.Event()
    lock = threading.Lock()
    done = 0
    fail_streak = 0

    def worker(tile: Tuple[int, int, int]) -> Dict[str, Any]:
        if stop.is_set():
            return {"status": "stopped", "bytes": 0, "tile": tile}
        return download_tile(client, args, out, kind, tile)

    ex = cf.ThreadPoolExecutor(max_workers=max(1, args.workers))
    try:
        for chunk in chunks(list(tiles), max(args.workers * 8, 100)):
            if stop.is_set():
                break
            futures = {ex.submit(worker, t): t for t in chunk}
            for fut in cf.as_completed(futures):
                try:
                    res = fut.result()
                except AuthError as exc:
                    stop.set()
                    stats["stopped"] = str(exc)
                    break
                except QuotaError as exc:
                    stop.set()
                    stats["stopped"] = str(exc)
                    break
                except Exception as exc:
                    res = {"status": "fail", "bytes": 0, "tile": futures[fut], "error": str(exc)}
                status = res.get("status", "fail")
                with lock:
                    done += 1
                    if status == "ok":
                        stats["ok"] += 1
                        stats["bytes"] += res.get("bytes", 0)
                        fail_streak = 0
                        budget_left -= args.cost_per_tile
                    elif status == "skip":
                        stats["skip"] += 1
                        fail_streak = 0
                    elif status == "blank":
                        stats["blank"] += 1
                        fail_streak = 0
                    elif status != "stopped":
                        stats["fail"] += 1
                        fail_streak += 1
                    if done % max(1, args.progress_every) == 0 or done == total:
                        elapsed = max(time.time() - start, 0.1)
                        rate = done / elapsed
                        eta = (total - done) / rate if rate > 0 else 0
                        print(
                            f"\r  {kind}: {done:,}/{total:,}  "
                            f"ok={stats['ok']:,} skip={stats['skip']:,} "
                            f"blank={stats['blank']:,} fail={stats['fail']:,}  "
                            f"{rate:.1f}/s ETA {eta/60:.1f}min   ",
                            end="", flush=True,
                        )
                if kind == "imagery" and budget_left <= 0:
                    stop.set()
                    stats["stopped"] = "达到本次额度上限"
                    break
                if fail_streak >= args.max_fail_streak:
                    stop.set()
                    stats["stopped"] = f"连续失败 {fail_streak} 个瓦片"
                    break
            if stop.is_set():
                break
            # 定期核对平台真实余额
            if (kind == "imagery" and not getattr(args, "ignore_account_quota", False)
                    and stats["ok"] and stats["ok"] % args.quota_check_every < len(chunk)):
                try:
                    q = client.quota()
                    used = float(q.get("usedCount") or 0)
                    flow_sum = float(q.get("flowSum") or 0)
                    log(f"平台余额: {used:,.1f}/{flow_sum:,.0f}，剩余 {flow_sum - used:,.1f}")
                    if q.get("isFlowExhausted"):
                        stop.set()
                        stats["stopped"] = "平台流量已耗尽"
                        break
                except Exception as exc:
                    log(f"余额查询失败（继续下载）: {exc}")
    finally:
        ex.shutdown(wait=True, cancel_futures=True)
        print("")
    stats["elapsed"] = time.time() - start
    return stats


def cmd_download(args: argparse.Namespace) -> int:
    client = make_client(args)
    user = client.login_info()
    info = client.find_map(args.map_name)
    client.user_tk()
    # 支持换 tk/mk 续传：只覆盖瓦片请求参数，不影响账号/余额查询。
    if args.tile_tk:
        client.tk = args.tile_tk.strip()
    if args.tile_sk:
        client.sk = args.tile_sk.strip()
    if args.tile_mk:
        info = dict(info)
        info["iv"] = args.tile_mk.strip()
        client.map_info = info
    if args.tile_tk or args.tile_mk:
        print(f"瓦片参数覆盖: tk={client.tk[:8]}… mk={client.map_info.get('iv')}")
    q = client.quota()

    aoi = AOI(geojson_to_geometry(load_geojson(args.aoi)), "主区域")
    detail = None
    if args.detail_aoi:
        detail = AOI(geojson_to_geometry(load_geojson(args.detail_aoi)), "核心区")
    zooms = parse_zooms(args.zooms)
    specs = build_zoom_specs(zooms, aoi, detail, args.detail_zoom)
    specs.sort(key=lambda item: item[0], reverse=(args.order == "desc"))

    out = Path(args.out)
    out.mkdir(parents=True, exist_ok=True)
    save_geojson(aoi.geom, out / "aoi.geojson")
    if detail:
        save_geojson(detail.geom, out / "detail_aoi.geojson")

    flow_sum = float(q.get("flowSum") or 0)
    used = float(q.get("usedCount") or 0)
    remaining = max(0.0, flow_sum - used)
    if args.ignore_account_quota:
        # 新 tk 可能对应另一份流量包；不要用旧账号余额提前截断。
        budget_left = max(0.0, args.daily_budget - args.reserve)
    else:
        daily_budget = min(args.daily_budget, remaining)
        budget_left = max(0.0, daily_budget - args.reserve)

    print("")
    print("=" * 70)
    print("开始下载")
    print("=" * 70)
    print(f"图源: {info.get('name')} ({info.get('resolution')} m)")
    print(f"账号: {mask_account(str(user.get('account') or ''))}")
    print(f"平台余额: {used:,.1f}/{flow_sum:,.0f} 次，本次预算 {budget_left:,.1f} 次")
    if args.ignore_account_quota:
        print("注意: 已忽略旧账号余额检查，实际是否耗尽以瓦片服务返回为准。")
    print(f"输出: {out.resolve()}")
    print(f"图层: {args.layers}，缩放: {zooms}，detail_zoom={args.detail_zoom}")

    layers = [x.strip() for x in args.layers.split(",") if x.strip()]
    priority = parse_point(args.priority_point) or aoi.centroid()

    for kind in layers:
        if kind not in ("imagery", "labels"):
            raise CrawlerError(f"未知图层: {kind}")
        print("")
        print(f"--- {kind} ---")
        for z, a, coverage in specs:
            if args.max_zoom is not None and z > args.max_zoom:
                continue
            log(f"枚举 z{z} {coverage} 瓦片 ...")
            tiles = list(a.iter_tiles(z))
            if not tiles:
                log(f"z{z} 无瓦片，跳过")
                continue
            # 离优先点近的先下，保证断点/额度不足时核心区完整
            tiles.sort(key=lambda t: (
                (tile_center_lonlat(*t)[0] - priority[0]) ** 2
                + (tile_center_lonlat(*t)[1] - priority[1]) ** 2
            ))
            if args.max_tiles:
                tiles = tiles[:args.max_tiles]
            cost_all = len(tiles) * args.cost_per_tile
            print("")
            print(f"z{z} {coverage}: {len(tiles):,} 瓦片, 预计 {cost_all:,.1f} 次")
            if kind == "imagery":
                existing = 0 if args.overwrite else sum(1 for t in tiles if existing_tile(out, kind, *t))
                new_needed = len(tiles) - existing
                cost_new = new_needed * args.cost_per_tile
                if existing:
                    print(f"  已有 {existing:,} 个，本次实际需下 {new_needed:,} 个，约 {cost_new:,.1f} 次")
                if cost_new <= 0:
                    print("  本层已全部存在，跳过下载。")
                    continue
                if cost_new > budget_left:
                    if not args.partial_zoom:
                        print(f"  预算不足（需 {cost_new:,.1f}，剩余 {budget_left:,.1f}），按 --no-partial-zoom 跳过。")
                        continue
                    print(
                        f"  预算只够约 {int(budget_left / args.cost_per_tile):,} 个新瓦片，"
                        f"按优先级部分下载。"
                    )
                    # 不在这里截断：已有瓦片免费跳过，download_batch 会在真实预算耗尽时停。
            stats = download_batch(client, args, out, kind, tiles, budget_left if kind == "imagery" else 0)
            print(
                f"  z{z} 完成: ok={stats['ok']:,} skip={stats['skip']:,} "
                f"blank={stats['blank']:,} fail={stats['fail']:,} "
                f"下载 {human_size(stats['bytes'])} 用时 {stats['elapsed']/60:.1f} 分钟"
            )
            if kind == "imagery":
                budget_left -= stats["ok"] * args.cost_per_tile
            if stats.get("stopped"):
                print(f"  停止原因: {stats['stopped']}")
                break
            if kind == "imagery" and budget_left <= 0:
                print("  本次预算已用完。")
                break

    # 保存元数据
    try:
        q_after = client.quota()
    except Exception:
        q_after = {}
    meta = {
        "downloaded_at": datetime.now(timezone.utc).isoformat(),
        "map": {
            "name": info.get("name"),
            "iv": info.get("iv"),
            "mkType": info.get("mkType"),
            "resolution": info.get("resolution"),
            "review_number": "GS(2026)4301号",
        },
        "aoi": {"name": "桑珠孜区", "area_km2": aoi.area_km2, "bounds": list(aoi.bounds)},
        "detail_aoi": (
            {"name": "核心区", "area_km2": detail.area_km2, "bounds": list(detail.bounds)}
            if detail else None
        ),
        "zooms": zooms,
        "detail_zoom": args.detail_zoom,
        "layers": layers,
        "quota_before": q,
        "quota_after": q_after,
        "tile_scheme": "XYZ / TMS (image URL uses {-y})",
        "labels": {
            "source": "天地图 cva_w 注记",
            "url_template": args.label_url,
        },
    }
    (out / "metadata.json").write_text(
        json.dumps(meta, ensure_ascii=False, indent=2), encoding="utf-8"
    )
    print("")
    print(f"完成。元数据: {out / 'metadata.json'}")
    if q_after:
        print(
            f"平台余额: {float(q_after.get('usedCount') or 0):,.1f}/"
            f"{float(q_after.get('flowSum') or 0):,.0f} 次"
        )
    return 0


# ---------------------------------------------------------------------------
# 预览拼接
# ---------------------------------------------------------------------------

def lonlat_to_pixel(lon: float, lat: float, z: int) -> Tuple[float, float]:
    n = 256.0 * (1 << z)
    px = (lon + 180.0) / 360.0 * n
    r = math.radians(max(min(lat, 85.05112878), -85.05112878))
    py = (1.0 - math.log(math.tan(r) + 1.0 / math.cos(r)) / math.pi) / 2.0 * n
    return px, py


def make_aoi_mask(aoi: AOI, z: int, x0: int, y0: int, tile_px: int, width: int, height: int) -> Any:
    mask = Image.new("L", (width, height), 0)
    draw = ImageDraw.Draw(mask)
    for poly in aoi.polygons:
        def to_px(coords: Any) -> List[Tuple[float, float]]:
            pts = []
            for lon, lat in coords:
                px, py = lonlat_to_pixel(lon, lat, z)
                pts.append(((px - x0 * 256.0) * tile_px / 256.0,
                            (py - y0 * 256.0) * tile_px / 256.0))
            return pts
        ext = to_px(list(poly.exterior.coords))
        if len(ext) >= 3:
            draw.polygon(ext, fill=255)
        for interior in poly.interiors:
            hole = to_px(list(interior.coords))
            if len(hole) >= 3:
                draw.polygon(hole, fill=0)
    return mask


def cmd_stitch(args: argparse.Namespace) -> int:
    if Image is None:
        raise CrawlerError("缺少 Pillow，请先 pip install Pillow")
    out = Path(args.out)
    if not out.exists():
        raise CrawlerError(f"目录不存在: {out}")
    aoi_path = Path(args.aoi) if args.aoi else out / "aoi.geojson"
    if not aoi_path.exists():
        raise CrawlerError(f"找不到 AOI: {aoi_path}")
    aoi = AOI(geojson_to_geometry(load_geojson(str(aoi_path))), "主区域")
    detail = None
    detail_path = Path(args.detail_aoi) if args.detail_aoi else out / "detail_aoi.geojson"
    if detail_path.exists():
        detail = AOI(geojson_to_geometry(load_geojson(str(detail_path))), "核心区")
    zooms = parse_zooms(args.zooms)
    preview_dir = out / "preview"
    preview_dir.mkdir(parents=True, exist_ok=True)

    for z in zooms:
        a = detail if (detail and args.detail_zoom is not None and z == args.detail_zoom) else aoi
        minx, miny, maxx, maxy = a.bounds
        n = 1 << z
        x0 = max(0, int((minx + 180.0) / 360.0 * n))
        x1 = min(n - 1, int((maxx + 180.0) / 360.0 * n))

        def lat2y(lat: float) -> int:
            r = math.radians(max(min(lat, 85.05112878), -85.05112878))
            return int((1.0 - math.log(math.tan(r) + 1.0 / math.cos(r)) / math.pi) / 2.0 * n)

        y0 = max(0, lat2y(maxy))
        y1 = min(n - 1, lat2y(miny))
        full_w = (x1 - x0 + 1) * 256
        full_h = (y1 - y0 + 1) * 256
        scale = min(1.0, args.max_px / max(full_w, full_h))
        tile_px = max(1, int(round(256 * scale)))
        width = (x1 - x0 + 1) * tile_px
        height = (y1 - y0 + 1) * tile_px

        if width < 2 or height < 2:
            continue
        if args.max_stitch_tiles and (x1 - x0 + 1) * (y1 - y0 + 1) > args.max_stitch_tiles:
            print(f"z{z}: 跳过（瓦片范围过大）")
            continue

        log(f"拼接 z{z} {a.name}: {width}x{height}px (tile {tile_px}px)")
        imagery = Image.new("RGBA", (width, height), (0, 0, 0, 0))
        labels = Image.new("RGBA", (width, height), (0, 0, 0, 0))
        missing = 0
        for x in range(x0, x1 + 1):
            for y in range(y0, y1 + 1):
                px = (x - x0) * tile_px
                py = (y - y0) * tile_px
                # 影像
                p = None
                for ext in ("webp", "png", "jpg", "jpeg"):
                    cand = tile_path(out, "imagery", z, x, y, ext)
                    if cand.exists() and cand.stat().st_size > 0:
                        p = cand
                        break
                if p:
                    try:
                        im = Image.open(p).convert("RGBA")
                        if im.size != (tile_px, tile_px):
                            im = im.resize((tile_px, tile_px), Image.Resampling.LANCZOS)
                        imagery.alpha_composite(im, (px, py))
                    except Exception:
                        missing += 1
                else:
                    missing += 1
                # 标注
                lp = tile_path(out, "labels", z, x, y, "png")
                if lp.exists() and lp.stat().st_size > 0:
                    try:
                        im = Image.open(lp).convert("RGBA")
                        if im.size != (tile_px, tile_px):
                            im = im.resize((tile_px, tile_px), Image.Resampling.LANCZOS)
                        labels.alpha_composite(im, (px, py))
                    except Exception:
                        pass

        mask = make_aoi_mask(a, z, x0, y0, tile_px, width, height)
        if args.no_clip:
            mask = Image.new("L", (width, height), 255)
        imagery.putalpha(mask)
        labels.putalpha(Image.composite(labels.getchannel("A"), Image.new("L", (width, height), 0), mask))

        # 影像 PNG（带透明边界）
        img_path = preview_dir / f"imagery_z{z}.png"
        imagery.save(img_path)
        # 影像 JPG（白底）
        bg = Image.new("RGB", (width, height), (255, 255, 255))
        bg.paste(imagery, mask=imagery.getchannel("A"))
        bg.save(preview_dir / f"imagery_z{z}.jpg", quality=90, optimize=True)
        # 标注 PNG
        labels.save(preview_dir / f"labels_z{z}.png")
        # 合成
        comp = Image.alpha_composite(imagery, labels)
        bg2 = Image.new("RGB", (width, height), (255, 255, 255))
        bg2.paste(comp, mask=comp.getchannel("A"))
        bg2.save(preview_dir / f"composite_z{z}.jpg", quality=90, optimize=True)
        print(
            f"  z{z} 完成: {img_path.name}, 缺瓦片 {missing:,}, "
            f"影像 {human_size((preview_dir / f'imagery_z{z}.png').stat().st_size)}"
        )
    print(f"\n预览目录: {preview_dir}")
    return 0


# ---------------------------------------------------------------------------
# info
# ---------------------------------------------------------------------------

def cmd_info(args: argparse.Namespace) -> int:
    client = make_client(args)
    user = client.login_info()
    print(f"账号: {mask_account(str(user.get('account') or ''))}")
    print(f"userId: {user.get('userId')}  实名: {user.get('authentication')}  VIP: {user.get('vipLevel')}")
    leaves = []

    def walk(nodes: Any) -> None:
        if not isinstance(nodes, list):
            return
        for node in nodes:
            if isinstance(node, dict) and isinstance(node.get("value"), list):
                walk(node["value"])
            elif isinstance(node, dict) and node.get("iv"):
                leaves.append(node)

    walk(client.series("basic"))
    print("\n基础图源（basic）：")
    for x in leaves:
        print(f"  {x.get('name')}  分辨率={x.get('resolution')}m  mkType={x.get('mkType')}  iv={x.get('iv')}")
    if leaves:
        m = next((x for x in leaves if x.get("name") == args.map_name), leaves[0])
        client.map_info = m
        q = client.quota()
        print(
            f"\n流量: {q.get('flowType')} {float(q.get('usedCount') or 0):,.1f}/"
            f"{float(q.get('flowSum') or 0):,.0f} 次  耗尽={q.get('isFlowExhausted')}"
        )
    return 0


# ---------------------------------------------------------------------------
# CLI
# ---------------------------------------------------------------------------

def add_common(p: argparse.ArgumentParser) -> None:
    p.add_argument("--token", help="登录 token（也可用环境变量 JL1_TOKEN）")
    p.add_argument("--token-file", help="保存 token 的文本文件")
    p.add_argument("--timeout", type=float, default=40.0, help="HTTP 超时秒数")
    p.add_argument("--proxy", help="HTTP(S) 代理，如 http://127.0.0.1:7890")
    p.add_argument("--map-name", default=DEFAULT_MAP, help="图源名称")


def add_aoi_args(p: argparse.ArgumentParser, required: bool = True) -> None:
    p.add_argument("--aoi", required=required, help="主区域 GeoJSON 文件路径（或内联 JSON）")
    p.add_argument("--detail-aoi", help="最高清核心区 GeoJSON；仅对 --detail-zoom 生效")
    p.add_argument("--detail-zoom", type=int, default=18, help="核心区对应的缩放级别，默认 18")
    p.add_argument("--zooms", default="10-18", help="缩放范围，如 10-18 或 12,14,16")
    p.add_argument("--cost-per-tile", type=float, default=0.5, help="平台计费：次/瓦片，默认 0.5")
    p.add_argument("--avg-tile-kb", type=float, default=8.0, help="估算存储用的平均瓦片 KB，默认 8")


def build_parser() -> argparse.ArgumentParser:
    p = argparse.ArgumentParser(
        description="吉林一号实验室切片影像 / 地名标注下载器",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog=__doc__,
    )
    sub = p.add_subparsers(dest="cmd", required=True)

    info = sub.add_parser("info", help="查看账号、图源和余额")
    add_common(info)
    info.set_defaults(func=cmd_info)

    plan = sub.add_parser("plan", help="只计算瓦片数、流量和存储，不下载")
    add_common(plan)
    add_aoi_args(plan)
    plan.add_argument("--out", default="./jl1_plan", help="计划输出目录")
    plan.set_defaults(func=cmd_plan)

    dl = sub.add_parser("download", help="下载瓦片")
    add_common(dl)
    add_aoi_args(dl)
    dl.add_argument("--out", required=True, help="输出目录")
    dl.add_argument("--layers", default="imagery", help="imagery / labels / imagery,labels")
    dl.add_argument("--workers", type=int, default=16, help="并发线程数，默认 16")
    dl.add_argument("--order", choices=("desc", "asc"), default="desc",
                    help="缩放顺序：desc=最高清优先（默认），asc=从低到高")
    dl.add_argument("--progress-every", type=int, default=100, help="每 N 个瓦片打印一次进度")
    dl.add_argument("--retries", type=int, default=3, help="单瓦片重试次数")
    dl.add_argument("--daily-budget", type=float, default=39000, help="本次最多消耗的流量（次）")
    dl.add_argument("--reserve", type=float, default=500, help="保留余额，默认 500")
    dl.add_argument("--tile-tk", help="覆盖 getUserTk 返回的瓦片 tk（换账号/换流量包时用）")
    dl.add_argument("--tile-sk", help="覆盖签名 sk；通常不用改，除非新 tk 对应不同 sk")
    dl.add_argument("--tile-mk", help="覆盖瓦片图源 mk/iv（例如 87ef1846fbacfb91a8f45eff68115cd7）")
    dl.add_argument("--ignore-account-quota", action="store_true",
                    help="不再用当前账号余额限制下载；由瓦片服务实际返回决定何时停，配合 --tile-tk")
    dl.add_argument("--overwrite", action="store_true",
                    help="覆盖已存在的瓦片；用于清理占位图/参数错误图后重下")
    dl.add_argument("--max-tiles", type=int, default=0, help="本次最多下载瓦片数，0 为不限")
    dl.add_argument("--max-zoom", type=int, default=None, help="临时限制最高缩放")
    dl.add_argument("--partial-zoom", action="store_true", default=True,
                    help="额度不足时按优先级部分下载当前层（默认开启）")
    dl.add_argument("--no-partial-zoom", dest="partial_zoom", action="store_false",
                    help="额度不足时整层跳过")
    dl.add_argument("--priority-point", help="优先下载的中心点，如 88.88,29.27")
    dl.add_argument("--quota-check-every", type=int, default=500, help="每 N 个瓦片核对一次余额")
    dl.add_argument("--max-fail-streak", type=int, default=30, help="连续失败多少次停止")
    dl.add_argument("--label-url", default=DEFAULT_LABEL_URL, help="标注瓦片 URL 模板")
    dl.add_argument("--label-tk", default=DEFAULT_LABEL_TK, help="天地图 token")
    dl.add_argument("--label-skip-blank", action="store_true", default=True,
                    help="跳过全透明标注瓦片（默认开启）")
    dl.add_argument("--no-label-skip-blank", dest="label_skip_blank", action="store_false")
    dl.set_defaults(func=cmd_download)

    st = sub.add_parser("stitch", help="把已下载瓦片拼成低分辨率预览图")
    st.add_argument("--out", required=True, help="download 的输出目录")
    st.add_argument("--aoi", help="主区域 GeoJSON，默认用输出目录里的 aoi.geojson")
    st.add_argument("--detail-aoi", help="核心区 GeoJSON，默认用 detail_aoi.geojson")
    st.add_argument("--detail-zoom", type=int, default=18)
    st.add_argument("--zooms", default="10-17", help="要拼的缩放级别")
    st.add_argument("--max-px", type=int, default=8192, help="预览图最长边像素")
    st.add_argument("--max-stitch-tiles", type=int, default=120000, help="瓦片范围超过此值跳过")
    st.add_argument("--no-clip", action="store_true", help="不按边界裁剪")
    st.set_defaults(func=cmd_stitch)
    return p


def main(argv: Optional[Sequence[str]] = None) -> int:
    parser = build_parser()
    args = parser.parse_args(argv)
    try:
        return int(args.func(args) or 0)
    except KeyboardInterrupt:
        eprint("\n已中断。已下载文件保留，可重新运行续传。")
        return 130
    except CrawlerError as exc:
        eprint(f"\n错误: {exc}")
        return 2
    except Exception as exc:
        eprint(f"\n未预期错误: {type(exc).__name__}: {exc}")
        if os.environ.get("JL1_DEBUG"):
            import traceback
            traceback.print_exc()
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
