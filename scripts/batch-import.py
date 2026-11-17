#!/usr/bin/env python3
"""批量导入工具：复用 hbtool 现有 HTTP API，把清单(manifest)写入运行中的服务。

设计要点
- 不新增服务端接口：创建社区/类型/标签、上传照片、建点位、排查、活动、整治、事项
  全部走现有 API（/api/communities、/api/photos 等）。
- 图片压缩在导入端完成（Pillow），与本项目“压缩在客户端做”的既有架构一致；
  默认压缩到长边 1600、质量取设置里的 jpegQuality，可用 --no-compress 存原图。
- 幂等：每次成功写入都在清单旁生成 `<manifest>.journal.json`，重复执行会跳过已完成
  的条目；同一批次的点位 key 也会被后置的排查/活动/整治引用。
- 分析类任务由 agent 生成 manifest，本工具只负责校验、压缩、写入和报告。

用法
    python scripts/batch-import.py bootstrap --server http://127.0.0.1:5181
    python scripts/batch-import.py apply --manifest import/manifests/2026环保.json \
        --server http://127.0.0.1:5181 [--dry-run] [--no-compress] [--quality 70]
    python scripts/batch-import.py old-db --app-dir import/old-version/环保日常排查整治整改 \
        --source "旧版 2026-10" --out import/manifests/old-version.json
"""
from __future__ import annotations

import argparse
import base64
import io
import json
import os
import re
import shutil
import sqlite3
import sys
import tempfile
import urllib.error
import urllib.request
from pathlib import Path

if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    sys.stderr.reconfigure(encoding="utf-8", errors="replace")

try:
    from PIL import Image
except ImportError:  # pragma: no cover - 运行环境缺少 Pillow 时给出明确提示
    Image = None

MAX_PHOTO_BYTES = 10 * 1024 * 1024
MAX_SIDE = 1600
KINDS = ("point", "inspection", "activity", "campaign", "matter")


class ImportError_(Exception):
    pass


# ---------------------------------------------------------------- HTTP client
class Api:
    def __init__(self, base: str, timeout: float = 60.0):
        self.base = base.rstrip("/")
        self.timeout = timeout

    def call(self, method: str, path: str, payload=None, raw: bytes | None = None,
             content_type: str = "application/json"):
        data = None
        headers = {}
        if payload is not None:
            data = json.dumps(payload, ensure_ascii=False).encode("utf-8")
            headers["Content-Type"] = content_type
        elif raw is not None:
            data = raw
            headers["Content-Type"] = content_type
        request = urllib.request.Request(self.base + path, data=data, headers=headers, method=method)
        try:
            with urllib.request.urlopen(request, timeout=self.timeout) as response:
                body = response.read()
                status = response.status
        except urllib.error.HTTPError as error:
            body = error.read()
            status = error.code
        text = body.decode("utf-8", "replace")
        try:
            parsed = json.loads(text) if text else None
        except json.JSONDecodeError:
            parsed = text
        if status >= 400:
            message = parsed.get("message") if isinstance(parsed, dict) else text
            raise ImportError_(f"{method} {path} -> {status}: {message}")
        return parsed

    def get(self, path: str):
        return self.call("GET", path)

    def post(self, path: str, payload):
        return self.call("POST", path, payload)

    def bootstrap(self):
        return self.get("/api/bootstrap")

    def settings(self):
        return self.get("/api/settings")


# ------------------------------------------------------------------- helpers
def normalize_date(value: str | None) -> str | None:
    """把 2026-03-24 / 2026/3/24 / 2026年3月24日 / 20260324 统一成 yyyy-MM-dd。"""
    if value is None:
        return None
    text = str(value).strip()
    if not text:
        return None
    match = re.match(r"^(\d{4})\D*(\d{1,2})\D*(\d{1,2})", text)
    if not match:
        raise ImportError_(f"无法识别的日期：{value}")
    year, month, day = (int(part) for part in match.groups())
    return f"{year:04d}-{month:02d}-{day:02d}"


def add_days(date: str, days: int) -> str:
    import datetime
    value = datetime.date.fromisoformat(date) + datetime.timedelta(days=days)
    return value.isoformat()


MATERIALS_ROOT = Path(".")


def resolve_photo_path(raw: str) -> Path:
    text = str(raw).replace("\\", "/").lstrip("/")
    path = Path(text)
    if path.is_absolute():
        return path
    return MATERIALS_ROOT / path


def read_photo_bytes(entry: dict, compress: bool, quality: int) -> tuple[bytes, dict]:
    """返回 (jpeg 字节, 统计信息)。entry 支持 path / base64；相对路径按材料根目录解析。"""
    if entry.get("path"):
        path = resolve_photo_path(entry["path"])
        if not path.is_file():
            raise ImportError_(f"照片不存在：{path}")
        data = path.read_bytes()
        origin = str(path)
    elif entry.get("base64"):
        data = base64.b64decode(entry["base64"])
        origin = "base64"
    else:
        raise ImportError_("照片条目需要 path 或 base64")
    before = len(data)
    if not (data[:2] == b"\xff\xd8"):
        raise ImportError_(f"只接受 JPEG 照片：{origin}")
    should_compress = entry.get("compress", compress)
    if should_compress:
        if Image is None:
            raise ImportError_("需要压缩图片但环境缺少 Pillow")
        try:
            image = Image.open(io.BytesIO(data))
            image.load()
            image = image.convert("RGB")
            image.thumbnail((MAX_SIDE, MAX_SIDE))
            output = io.BytesIO()
            image.save(output, "JPEG", quality=quality, optimize=True)
            data = output.getvalue()
        except OSError as error:
            raise ImportError_(f"图片解码失败（{origin}）：{error}") from error
    if len(data) > MAX_PHOTO_BYTES:
        raise ImportError_(f"照片超过 10 MB（{len(data)} 字节）：{origin}")
    return data, {"before": before, "after": len(data), "compressed": bool(should_compress)}


class Journal:
    """清单旁的幂等记录：key -> {kind, id}。"""

    def __init__(self, manifest: Path):
        self.path = manifest.with_suffix(manifest.suffix + ".journal.json")
        self.data: dict[str, dict] = {}
        if self.path.exists():
            self.data = json.loads(self.path.read_text(encoding="utf-8"))

    def get(self, kind: str, key: str | None) -> str | None:
        if not key:
            return None
        entry = self.data.get(f"{kind}:{key}")
        return entry["id"] if entry else None

    def put(self, kind: str, key: str | None, value: str):
        if not key:
            return
        self.data[f"{kind}:{key}"] = {"kind": kind, "id": value}
        self.save()

    def save(self):
        self.path.parent.mkdir(parents=True, exist_ok=True)
        temporary = self.path.with_suffix(self.path.suffix + ".tmp")
        temporary.write_text(json.dumps(self.data, ensure_ascii=False, indent=2), encoding="utf-8")
        os.replace(temporary, self.path)


class Importer:
    def __init__(self, api: Api, journal: Journal, compress: bool, quality: int, dry_run: bool, inspector: str = "城东街道"):
        self.api = api
        self.journal = journal
        self.compress = compress
        self.quality = quality
        self.dry_run = dry_run
        self.inspector = inspector
        self.ids: dict[str, dict[str, str]] = {}
        self.placeholder_points: dict[str, str] = {}
        self.summary = {"created": 0, "skipped": 0, "failed": 0, "photos": 0}
        self.errors: list[str] = []
        self.warnings: list[str] = []

    # ---------------------------------------------------------------- lookups
    def load(self):
        bootstrap = self.api.bootstrap()
        self.ids["community"] = {item["name"]: item["id"] for item in bootstrap["communities"]}
        self.ids["point_type"] = {item["name"]: item["id"] for item in bootstrap["pointTypes"]}
        self.ids["issue_type"] = {item["name"]: item["id"] for item in bootstrap["issueTypes"]}
        self.ids["activity_type"] = {item["name"]: item["id"] for item in bootstrap["activityTypes"]}
        self.ids["remediation_type"] = {item["name"]: item["id"] for item in bootstrap["remediationTypes"]}
        self.ids["tag"] = {item["name"]: item["id"] for item in bootstrap["tags"]}
        self.quality = self.api.settings().get("jpegQuality", self.quality) or self.quality
        return bootstrap

    def ensure(self, category: str, name: str | None, path: str) -> str | None:
        if name is None or str(name).strip() == "":
            return None
        name = str(name).strip()
        known = self.ids[category]
        if name in known:
            return known[name]
        if self.dry_run:
            self.ids[category][name] = "(dry-run)"
            return "(dry-run)"
        payload = {"name": name, "title": name} if category == "community" else {"name": name}
        created = self.api.post(path, payload)
        self.ids[category][name] = created["id"]
        return created["id"]

    def tag_ids(self, names) -> list[str]:
        result = []
        for name in names or []:
            value = self.ensure("tag", name, "/api/tags")
            if value:
                result.append(value)
        return result

    # ----------------------------------------------------------------- photos
    def photos(self, entries, phase: str, maximum: int) -> list[str]:
        entries = entries or []
        if len(entries) > maximum:
            raise ImportError_(f"{phase} 照片最多 {maximum} 张，当前 {len(entries)} 张")
        ids = []
        for entry in entries:
            data, stats = read_photo_bytes(entry, self.compress, self.quality)
            if self.dry_run:
                ids.append("(dry-run)")
                self.summary["photos"] += 1
                continue
            uploaded = self.api.call("PUT", f"/api/photos?phase={phase}", raw=data, content_type="image/jpeg")
            ids.append(uploaded["id"])
            self.summary["photos"] += 1
            if stats["compressed"] and stats["after"] < stats["before"]:
                pass
        return ids

    # ----------------------------------------------------------------- points
    def point(self, item: dict):
        key = item.get("key")
        existing = self.journal.get("point", key)
        if existing:
            self.summary["skipped"] += 1
            return existing
        if item.get("existingPointId"):
            # 合并阶段判定该点位已在库中，直接复用，不重复创建。
            self.journal.put("point", key, item["existingPointId"])
            self.summary["skipped"] += 1
            return item["existingPointId"]
        community = self.ensure("community", item.get("community"), "/api/communities")
        if community is None:
            raise ImportError_("点位缺少 community")
        point_type = self.ensure("point_type", item.get("type"), "/api/point-types")
        if point_type is None:
            raise ImportError_("点位缺少 type")
        photo_ids = self.photos(item.get("photos"), "point", 9)
        payload = {
            "name": item.get("name") or "",
            "communityId": community,
            "typeId": point_type,
            "address": item.get("address") or "地址未载明（材料未记载）",
            "contact": item.get("contact") or "",
            "phone": item.get("phone") or "",
            "frequency": item.get("frequency") or "irregular",
            "notes": item.get("notes") or "",
            "disabled": bool(item.get("disabled")),
            "longitude": item.get("longitude"),
            "latitude": item.get("latitude"),
            "photoIds": photo_ids,
            "tagIds": self.tag_ids(item.get("tags")),
        }
        if item.get("shape"):
            payload["shape"] = item["shape"]
        if self.dry_run:
            if key:
                self.placeholder_points[key] = "(dry-run)"
            self.summary["created"] += 1
            return "(dry-run)"
        created = self.api.post("/api/points", payload)
        self.journal.put("point", key, created["id"])
        self.summary["created"] += 1
        return created["id"]

    def resolve_point(self, item: dict) -> str:
        if item.get("pointId"):
            return item["pointId"]
        if item.get("pointKey"):
            value = self.journal.get("point", item["pointKey"]) or self.placeholder_points.get(item["pointKey"])
            if not value:
                raise ImportError_(f"找不到 pointKey={item['pointKey']}，请先导入点位")
            return value
        raise ImportError_("需要 pointKey 或 pointId")

    # ------------------------------------------------------------ inspections
    def inspection(self, item: dict):
        key = item.get("key")
        if self.journal.get("inspection", key):
            self.summary["skipped"] += 1
            return
        point_id = self.resolve_point(item)
        date = normalize_date(item.get("date"))
        if not date:
            raise ImportError_("排查缺少 date")
        issues = item.get("issues") or []
        issue_payload = []
        for issue in issues:
            deadline = normalize_date(issue.get("deadline")) or add_days(date, 7)
            if not normalize_date(issue.get("deadline")):
                self.warnings.append(f"{key}: 问题未给期限，按 {deadline} 处理")
            issue_payload.append({
                "description": issue.get("description") or "",
                "requirement": issue.get("requirement") or "",
                "deadline": deadline,
                "responsibleUnit": issue.get("responsibleUnit") or "",
                "issueTypeId": self.ensure("issue_type", issue.get("issueType") or "环境卫生", "/api/issue-types"),
                "photoIds": self.photos(issue.get("photos"), "site", 9),
            })
        payload = {
            "pointId": point_id,
            "date": date,
            "inspector": item.get("inspector") or self.inspector,
            "result": item.get("result") or ("issues" if issue_payload else "normal"),
            "notes": item.get("notes") or "",
            "noticeNumber": item.get("noticeNumber") or "",
            "source": item.get("source") or "routine",
            "photoIds": self.photos(item.get("photos"), "site", 20),
            "tagIds": self.tag_ids(item.get("tags")),
            "issues": issue_payload,
        }
        if self.dry_run:
            self.summary["created"] += 1
            return
        created = self.api.post("/api/inspections", payload)
        self.journal.put("inspection", key, created["inspection"]["id"])
        self.summary["created"] += 1

    # -------------------------------------------------------------- activities
    def activity(self, item: dict):
        key = item.get("key")
        if self.journal.get("activity", key):
            self.summary["skipped"] += 1
            return
        type_id = self.ensure("activity_type", item.get("type") or "其他", "/api/activity-types")
        payload = {
            "name": item.get("name") or "",
            "typeId": type_id,
            "date": normalize_date(item.get("date")),
            "endDate": normalize_date(item.get("endDate")),
            "content": item.get("content") or "",
            "communityId": self.ensure("community", item.get("community"), "/api/communities"),
            "location": item.get("location") or "",
            "organizer": item.get("organizer") or "",
            "participants": item.get("participants") or "",
            "participantCount": item.get("participantCount"),
            "outcome": item.get("outcome") or "",
            "notes": item.get("notes") or "",
            "photoIds": self.photos(item.get("photos"), "activity", 20),
            "tagIds": self.tag_ids(item.get("tags")),
        }
        if item.get("pointKey") or item.get("pointId"):
            payload["pointId"] = self.resolve_point(item)
        if self.dry_run:
            self.summary["created"] += 1
            return
        created = self.api.post("/api/activities", payload)
        self.journal.put("activity", key, created["activity"]["id"])
        self.summary["created"] += 1

    # --------------------------------------------------------------- campaigns
    def campaign(self, item: dict):
        key = item.get("key")
        if self.journal.get("campaign", key):
            self.summary["skipped"] += 1
            return
        point_id = self.resolve_point(item)
        remediation_id = self.ensure("remediation_type", item.get("type") or "其他", "/api/remediation-types")
        group = lambda name, phase, limit: self.photos(item.get(name), phase, limit)  # noqa: E731
        payload = {
            "name": item.get("name") or "",
            "typeId": self.ensure("activity_type", "整治", "/api/activity-types"),
            "date": normalize_date(item.get("date")),
            "endDate": normalize_date(item.get("endDate")),
            "content": item.get("content") or "",
            "pointId": point_id,
            "organizer": item.get("organizer") or "",
            "participants": item.get("participants") or "",
            "participantCount": item.get("participantCount"),
            "outcome": item.get("outcome") or "",
            "notes": item.get("notes") or "",
            "tagIds": self.tag_ids(item.get("tags")),
            "remediation": {
                "typeId": remediation_id,
                "vehicleCount": item.get("vehicleCount"),
                "garbageTons": item.get("garbageTons"),
                "areaSqm": item.get("areaSqm"),
                "beforePhotoIds": group("beforePhotos", "before", 20),
                "processPhotoIds": group("processPhotos", "process", 20),
                "afterPhotoIds": group("afterPhotos", "after", 20),
            },
        }
        if self.dry_run:
            self.summary["created"] += 1
            return
        created = self.api.post("/api/activities", payload)
        self.journal.put("campaign", key, created["activity"]["id"])
        self.summary["created"] += 1

    # ----------------------------------------------------------------- matters
    def matter(self, item: dict):
        key = item.get("key")
        if self.journal.get("matter", key):
            self.summary["skipped"] += 1
            return
        payload = {
            "title": item.get("title") or "",
            "source": item.get("source") or "other",
            "receivedDate": normalize_date(item.get("receivedDate")),
            "content": item.get("content") or "",
            "location": item.get("location") or "",
            "deadline": normalize_date(item.get("deadline")),
            "handler": item.get("handler") or "",
            "referenceNumber": item.get("referenceNumber") or "",
            "contact": item.get("contact") or "",
            "phone": item.get("phone") or "",
            "tagIds": self.tag_ids(item.get("tags")),
        }
        if item.get("pointKey") or item.get("pointId"):
            payload["pointId"] = self.resolve_point(item)
        if self.dry_run:
            self.summary["created"] += 1
            return
        created = self.api.post("/api/matters", payload)
        self.journal.put("matter", key, created["id"])
        self.summary["created"] += 1

    # -------------------------------------------------------------------- run
    def ensure_lists(self, manifest: dict):
        for name in manifest.get("communities") or []:
            self.ensure("community", name, "/api/communities")
        for name in manifest.get("pointTypes") or []:
            self.ensure("point_type", name, "/api/point-types")
        for name in manifest.get("issueTypes") or []:
            self.ensure("issue_type", name, "/api/issue-types")
        for name in manifest.get("activityTypes") or []:
            self.ensure("activity_type", name, "/api/activity-types")
        for name in manifest.get("remediationTypes") or []:
            self.ensure("remediation_type", name, "/api/remediation-types")
        for name in manifest.get("tags") or []:
            self.ensure("tag", name, "/api/tags")

    def run(self, manifest: dict, only: set[str] | None = None, limit: int | None = None):
        self.load()
        if not self.dry_run:
            self.ensure_lists(manifest)
        counts = {"point": 0, "inspection": 0, "activity": 0, "campaign": 0, "matter": 0}
        for item in manifest.get("items") or []:
            kind = item.get("kind")
            if kind not in KINDS:
                self.errors.append(f"未知的条目类型：{kind}")
                self.summary["failed"] += 1
                continue
            if only and kind not in only:
                continue
            if limit is not None and counts[kind] >= limit:
                continue
            counts[kind] += 1
            key = item.get("key") or f"{kind}-{counts[kind]}"
            try:
                if kind == "point":
                    self.point(item)
                elif kind == "inspection":
                    self.inspection(item)
                elif kind == "activity":
                    self.activity(item)
                elif kind == "campaign":
                    self.campaign(item)
                else:
                    self.matter(item)
            except ImportError_ as error:
                self.summary["failed"] += 1
                self.errors.append(f"[{kind}:{key}] {error}")
                print(f"  ! {kind} {key}: {error}", file=sys.stderr)
        return self.summary


# ------------------------------------------------------- 旧版本数据库转换器
def export_old_database(app_dir: Path, source: str) -> dict:
    """把旧版部署目录（data/app.db）转换成 manifest。"""
    data = app_dir / "data" / "app.db"
    if not data.is_file():
        raise ImportError_(f"未找到旧数据库：{data}")
    photos_root = app_dir / "photos"
    # 用清单目录旁的临时目录，避免系统临时目录在受限环境里不可写。
    temporary = app_dir.parent / ".import-tmp"
    shutil.rmtree(temporary, ignore_errors=True)
    temporary.mkdir(parents=True, exist_ok=True)
    try:
        copy = temporary / "app.db"
        shutil.copy2(data, copy)
        for suffix in ("-wal", "-shm"):
            extra = Path(str(data) + suffix)
            if extra.exists():
                shutil.copy2(extra, Path(str(copy) + suffix))
        connection = sqlite3.connect(copy)
        connection.row_factory = sqlite3.Row

        def rows(sql, parameters=()):
            return [dict(row) for row in connection.execute(sql, parameters)]

        tables = {row[0] for row in connection.execute("SELECT name FROM sqlite_master WHERE type='table'")}
        communities = rows("SELECT * FROM communities") if "communities" in tables else []
        point_types = rows("SELECT * FROM point_types") if "point_types" in tables else []
        campaign_types = rows("SELECT * FROM campaign_types") if "campaign_types" in tables else []
        points = rows("SELECT * FROM points") if "points" in tables else []
        inspections = rows("SELECT * FROM inspections") if "inspections" in tables else []
        issues = rows("SELECT * FROM issues") if "issues" in tables else []
        campaigns = rows("SELECT * FROM campaigns") if "campaigns" in tables else []
        photos = rows("SELECT * FROM photos") if "photos" in tables else []
        connection.close()
    finally:
        shutil.rmtree(temporary, ignore_errors=True)

    community_name = {row["id"]: row["name"] for row in communities}
    type_name = {row["id"]: row["name"] for row in point_types}
    campaign_type_name = {row["id"]: row["name"] for row in campaign_types}
    photo_by = {}
    for row in photos:
        path = photos_root / str(row["relative_path"]).replace("/", os.sep)
        photo_by.setdefault((row.get("point_id") or "", row.get("inspection_id") or "",
                             row.get("issue_id") or "", row.get("campaign_id") or ""), []).append(
            {"path": str(path), "compress": True} if path.exists() else None)

    def photos_for(**owner):
        key = tuple(owner.get(name) or "" for name in ("point_id", "inspection_id", "issue_id", "campaign_id"))
        return [item for item in photo_by.get(key, []) if item]

    items = []
    for row in points:
        items.append({
            "kind": "point",
            "key": f"old-point-{row['id']}",
            "name": row["name"],
            "community": community_name.get(row["community_id"], ""),
            "type": type_name.get(row["type_id"], ""),
            "address": row.get("address") or "",
            "contact": row.get("contact") or "",
            "phone": row.get("phone") or "",
            "frequency": row.get("frequency") or "irregular",
            "notes": row.get("notes") or "",
            "disabled": bool(row.get("disabled")),
            "longitude": row.get("longitude"),
            "latitude": row.get("latitude"),
            "photos": photos_for(point_id=row["id"]),
        })
    issues_by_inspection: dict[str, list] = {}
    for row in issues:
        issues_by_inspection.setdefault(row["inspection_id"], []).append({
            "description": row.get("description") or "",
            "requirement": row.get("requirement") or "",
            "deadline": row.get("deadline") or "",
            "responsibleUnit": row.get("responsible_unit") or "",
            "issueType": "环境卫生",
            "photos": photos_for(issue_id=row["id"]),
        })
    for row in inspections:
        items.append({
            "kind": "inspection",
            "key": f"old-inspection-{row['id']}",
            "pointKey": f"old-point-{row['point_id']}",
            "date": (row.get("inspection_date") or "")[:10],
            "inspector": row.get("inspector") or "",
            "result": row.get("result") or "normal",
            "notes": row.get("notes") or "",
            "noticeNumber": row.get("notice_number") or "",
            "photos": photos_for(inspection_id=row["id"]),
            "issues": issues_by_inspection.get(row["id"], []),
        })
    for row in campaigns:
        items.append({
            "kind": "campaign",
            "key": f"old-campaign-{row['id']}",
            "name": row.get("name") or "",
            "type": campaign_type_name.get(row.get("type_id"), row.get("type_name") or "其他"),
            "date": (row.get("campaign_date") or "")[:10],
            "endDate": (row.get("end_date") or "")[:10] or None,
            "content": row.get("content") or "",
            "pointKey": f"old-point-{row['point_id']}" if row.get("point_id") else None,
            "organizer": row.get("organizer") or "",
            "participants": row.get("participants") or "",
            "participantCount": row.get("participant_count"),
            "vehicleCount": row.get("vehicle_count"),
            "garbageTons": row.get("garbage_tons"),
            "areaSqm": row.get("area_sqm"),
            "outcome": row.get("outcome") or "",
            "notes": row.get("notes") or "",
            "beforePhotos": photos_for(campaign_id=row["id"]),
        })

    return {
        "source": source,
        "generatedFrom": str(app_dir),
        "communities": sorted({row["name"] for row in communities}),
        "pointTypes": sorted({row["name"] for row in point_types}),
        "remediationTypes": sorted({row["name"] for row in campaign_types}),
        "items": items,
    }


# ------------------------------------------------------------------ commands
def command_bootstrap(args):
    api = Api(args.server)
    bootstrap = api.bootstrap()
    print(json.dumps({
        "server": args.server,
        "communities": [{"id": item["id"], "name": item["name"]} for item in bootstrap["communities"]],
        "pointTypes": [item["name"] for item in bootstrap["pointTypes"]],
        "issueTypes": [item["name"] for item in bootstrap["issueTypes"]],
        "activityTypes": [item["name"] for item in bootstrap["activityTypes"]],
        "remediationTypes": [item["name"] for item in bootstrap["remediationTypes"]],
        "tags": [item["name"] for item in bootstrap["tags"]],
        "jpegQuality": bootstrap["settings"]["jpegQuality"],
        "today": bootstrap["today"],
    }, ensure_ascii=False, indent=2))


def command_apply(args):
    global MATERIALS_ROOT
    MATERIALS_ROOT = Path(args.materials).resolve()
    manifest_path = Path(args.manifest)
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    api = Api(args.server)
    journal = Journal(manifest_path)
    compress = False if args.no_compress else True
    importer = Importer(api, journal, compress, args.quality, args.dry_run, args.inspector)
    only = {part.strip() for part in args.only.split(",") if part.strip()} if args.only else None
    summary = importer.run(manifest, only=only, limit=args.limit)
    print(json.dumps({
        "source": manifest.get("source"),
        "dryRun": args.dry_run,
        "compress": compress,
        "materials": str(MATERIALS_ROOT),
        "summary": summary,
        "errors": importer.errors,
        "warnings": importer.warnings[:50],
        "journal": str(journal.path),
    }, ensure_ascii=False, indent=2))
    return 1 if importer.errors else 0


def command_old_db(args):
    manifest = export_old_database(Path(args.app_dir), args.source)
    output = Path(args.out)
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(manifest, ensure_ascii=False, indent=2), encoding="utf-8")
    kinds = {}
    for item in manifest["items"]:
        kinds[item["kind"]] = kinds.get(item["kind"], 0) + 1
    print(json.dumps({"out": str(output), "kinds": kinds}, ensure_ascii=False, indent=2))


def command_verify(args):
    api = Api(args.server)
    health = api.get("/api/health")
    bootstrap = api.bootstrap()
    print(json.dumps({
        "health": health,
        "communities": len(bootstrap["communities"]),
        "points": api.get("/api/points?pageSize=1")["total"],
        "inspections": api.get("/api/inspections?pageSize=1")["total"],
    }, ensure_ascii=False))


def main(argv=None):
    parser = argparse.ArgumentParser(description="hbtool 批量导入工具")
    sub = parser.add_subparsers(dest="command", required=True)

    bootstrap = sub.add_parser("bootstrap", help="打印服务端已有的社区/类型/标签")
    bootstrap.add_argument("--server", default="http://127.0.0.1:5181")
    bootstrap.set_defaults(func=command_bootstrap)

    apply_parser = sub.add_parser("apply", help="执行清单导入")
    apply_parser.add_argument("--manifest", required=True)
    apply_parser.add_argument("--server", default="http://127.0.0.1:5181")
    apply_parser.add_argument("--dry-run", action="store_true")
    apply_parser.add_argument("--no-compress", action="store_true", help="保留原始照片字节")
    apply_parser.add_argument("--quality", type=int, default=75)
    apply_parser.add_argument("--only", default="", help="只处理 point,inspection,activity,campaign,matter 中的若干类")
    apply_parser.add_argument("--limit", type=int, default=None)
    apply_parser.add_argument("--inspector", default="城东街道", help="材料未写排查人时使用")
    apply_parser.add_argument("--materials", default=str(Path("import/work/2026环保")),
                              help="清单里照片相对路径的根目录")
    apply_parser.set_defaults(func=command_apply)

    old = sub.add_parser("old-db", help="把旧版部署目录转换成清单")
    old.add_argument("--app-dir", required=True)
    old.add_argument("--source", required=True)
    old.add_argument("--out", required=True)
    old.set_defaults(func=command_old_db)

    verify = sub.add_parser("verify", help="检查服务连通性")
    verify.add_argument("--server", default="http://127.0.0.1:5181")
    verify.set_defaults(func=command_verify)

    args = parser.parse_args(argv)
    try:
        result = args.func(args)
    except ImportError_ as error:
        print(f"导入失败：{error}", file=sys.stderr)
        return 2
    return result if isinstance(result, int) else 0


if __name__ == "__main__":
    sys.exit(main())
