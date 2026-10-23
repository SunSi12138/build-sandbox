#!/usr/bin/env python3
"""一次性离线迁移：旧结构 1/2/3 -> 新版结构 1。仅使用 Python 标准库。

源目录保持只读；输出必须为一个不存在的独立目录。
调用示例：python scripts/convert_database.py --source /old/hbtool --output /new/hbtool
"""
from __future__ import annotations

import argparse
import pathlib
import re
import shutil
import sqlite3
import sys
import uuid

APPLICATION_ID = 1397048143
ISSUE_NAMES = ("环境卫生", "垃圾堆放", "餐饮油烟", "扬尘污染", "水体污染", "噪声污染", "其他")
ACTIVITY_NAMES = ("工作部署会议", "宣传教育", "集中学习", "业务培训", "志愿服务", "其他")
CAMPAIGN_NAMES = ("卫生大扫除", "垃圾清运", "河道清理", "乱堆乱放清理", "小广告清除", "绿化补种", "其他")
PHOTO_PATH = re.compile(r"^\d{6}/[0-9a-f]{32}\.jpg$")


def columns(db: sqlite3.Connection, name: str) -> set[str]:
    return {row[1] for row in db.execute(f"PRAGMA table_info({name})")}


def add_column(db: sqlite3.Connection, table: str, name: str, definition: str) -> None:
    if name not in columns(db, table):
        db.execute(f"ALTER TABLE {table} ADD COLUMN {name} {definition}")


def require_good(db: sqlite3.Connection) -> None:
    if db.execute("PRAGMA integrity_check").fetchone()[0] != "ok":
        raise ValueError("数据库完整性检查未通过")
    if db.execute("PRAGMA foreign_key_check").fetchone():
        raise ValueError("数据库外键检查未通过")


def create_dict(db: sqlite3.Connection, table: str, names: tuple[str, ...]) -> None:
    db.execute(f"CREATE TABLE IF NOT EXISTS {table} (id TEXT PRIMARY KEY, name TEXT NOT NULL UNIQUE)")
    for name in names:
        if db.execute(f"SELECT 1 FROM {table} WHERE name=?", (name,)).fetchone() is None:
            db.execute(f"INSERT INTO {table}(id,name) VALUES(?,?)", (uuid.uuid4().hex, name))


def convert(db: sqlite3.Connection) -> dict[str, int]:
    required = {"settings", "communities", "point_types", "points", "inspections", "issues", "issue_actions", "photos"}
    actual = {row[0] for row in db.execute("SELECT name FROM sqlite_master WHERE type='table'")}
    version = db.execute("PRAGMA user_version").fetchone()[0]
    application = db.execute("PRAGMA application_id").fetchone()[0]
    if version not in (1, 2, 3) or application != APPLICATION_ID or not required <= actual:
        raise ValueError("旧库版本或结构不支持")
    if "activity_types" in actual or "activities" in actual or "issue_types" in actual:
        raise ValueError("已经是新版数据库，不能再次转换")
    require_good(db)
    counts = {table: db.execute(f"SELECT COUNT(*) FROM {table}").fetchone()[0]
              for table in ("points", "inspections", "issues", "issue_actions", "photos")}
    counts["campaigns"] = db.execute("SELECT COUNT(*) FROM campaigns").fetchone()[0] if "campaigns" in actual else 0

    db.execute("PRAGMA foreign_keys=OFF")
    db.execute("BEGIN IMMEDIATE")
    try:
        add_column(db, "points", "longitude", "REAL")
        add_column(db, "points", "latitude", "REAL")
        add_column(db, "points", "temporary", "INTEGER NOT NULL DEFAULT 0")
        create_dict(db, "issue_types", ISSUE_NAMES)
        create_dict(db, "activity_types", ACTIVITY_NAMES)
        create_dict(db, "campaign_types", CAMPAIGN_NAMES)
        db.execute("""
            CREATE TABLE activities (
                id TEXT PRIMARY KEY, name TEXT NOT NULL,
                type_id TEXT NOT NULL REFERENCES activity_types(id) ON DELETE RESTRICT, type_name TEXT NOT NULL,
                community_id TEXT REFERENCES communities(id) ON DELETE RESTRICT,
                community_name TEXT NOT NULL DEFAULT '', location TEXT NOT NULL DEFAULT '',
                activity_date TEXT NOT NULL, end_date TEXT, organizer TEXT NOT NULL DEFAULT '',
                participants TEXT NOT NULL DEFAULT '', participant_count INTEGER CHECK(participant_count IS NULL OR participant_count>=0),
                content TEXT NOT NULL, outcome TEXT NOT NULL DEFAULT '', notes TEXT NOT NULL DEFAULT '',
                CHECK(end_date IS NULL OR end_date>=activity_date)
            )
        """)
        other_id = db.execute("SELECT id FROM issue_types WHERE name='其他'").fetchone()[0]
        db.execute("""
            CREATE TABLE issues_new (
                id TEXT PRIMARY KEY, inspection_id TEXT NOT NULL REFERENCES inspections(id) ON DELETE RESTRICT,
                issue_type_id TEXT NOT NULL REFERENCES issue_types(id) ON DELETE RESTRICT,
                issue_type_name TEXT NOT NULL,
                description TEXT NOT NULL, requirement TEXT NOT NULL DEFAULT '', deadline TEXT NOT NULL,
                responsible_unit TEXT NOT NULL, status TEXT NOT NULL DEFAULT 'pending' CHECK(status IN ('pending','feedback','closed')),
                feedback_date TEXT, feedback_notes TEXT NOT NULL DEFAULT '', review_date TEXT,
                review_conclusion TEXT NOT NULL DEFAULT '', closed_date TEXT
            )
        """)
        db.execute("""
            INSERT INTO issues_new(id,inspection_id,issue_type_id,issue_type_name,description,requirement,deadline,
                responsible_unit,status,feedback_date,feedback_notes,review_date,review_conclusion,closed_date)
            SELECT id,inspection_id,?,'其他',description,requirement,deadline,responsible_unit,status,
                feedback_date,feedback_notes,review_date,review_conclusion,closed_date FROM issues
        """, (other_id,))
        db.execute("DROP TABLE issues")
        db.execute("ALTER TABLE issues_new RENAME TO issues")
        db.execute("CREATE INDEX ix_issues_status_deadline ON issues(status,deadline)")
        db.execute("CREATE INDEX ix_issues_inspection ON issues(inspection_id)")
        db.execute("CREATE INDEX ix_issues_type ON issues(issue_type_id)")
        db.execute("""
            CREATE TABLE campaigns_new (
                id TEXT PRIMARY KEY, name TEXT NOT NULL,
                point_id TEXT NOT NULL REFERENCES points(id) ON DELETE RESTRICT, point_name TEXT NOT NULL,
                type_id TEXT NOT NULL REFERENCES campaign_types(id) ON DELETE RESTRICT, type_name TEXT NOT NULL,
                community_id TEXT NOT NULL REFERENCES communities(id) ON DELETE RESTRICT, community_name TEXT NOT NULL,
                location TEXT NOT NULL, campaign_date TEXT NOT NULL, end_date TEXT,
                organizer TEXT NOT NULL DEFAULT '', participants TEXT NOT NULL DEFAULT '',
                participant_count INTEGER CHECK(participant_count IS NULL OR participant_count>=0),
                vehicle_count INTEGER CHECK(vehicle_count IS NULL OR vehicle_count>=0),
                garbage_tons REAL CHECK(garbage_tons IS NULL OR garbage_tons>=0),
                area_sqm REAL CHECK(area_sqm IS NULL OR area_sqm>=0),
                content TEXT NOT NULL, outcome TEXT NOT NULL DEFAULT '', notes TEXT NOT NULL DEFAULT '',
                CHECK(end_date IS NULL OR end_date>=campaign_date)
            )
        """)
        if counts["campaigns"]:
            source_columns = columns(db, "campaigns")
            point_types = db.execute("SELECT id FROM point_types ORDER BY rowid LIMIT 1").fetchone()
            if point_types is None:
                new_type = uuid.uuid4().hex
                db.execute("INSERT INTO point_types(id,name) VALUES(?,?)", (new_type, "其他"))
            else:
                new_type = point_types[0]
            db.row_factory = sqlite3.Row
            rows = list(db.execute("SELECT * FROM campaigns"))
            db.row_factory = None
            for row in rows:
                c = dict(row)
                point_id = c.get("point_id")
                point_name = c.get("point_name")
                if not point_id or not db.execute("SELECT 1 FROM points WHERE id=?", (point_id,)).fetchone():
                    point_id = uuid.uuid4().hex
                    point_name = c["name"]
                    db.execute("""
                        INSERT INTO points(id,name,community_id,type_id,address,contact,phone,frequency,notes,disabled,longitude,latitude,temporary)
                        VALUES(?,?,?,?,?,'','','irregular','',0,NULL,NULL,1)
                    """, (point_id, point_name, c["community_id"], new_type, c.get("location") or "活动地点未登记"))
                point_name = point_name or db.execute("SELECT name FROM points WHERE id=?", (point_id,)).fetchone()[0]
                payload = {key: c.get(key) for key in ("id", "name", "type_id", "type_name", "community_id",
                    "community_name", "location", "campaign_date", "end_date", "organizer", "participants",
                    "participant_count", "vehicle_count", "garbage_tons", "area_sqm", "content", "outcome", "notes")}
                payload.update(point_id=point_id, point_name=point_name)
                sql_keys = tuple(payload)
                db.execute("INSERT INTO campaigns_new(" + ",".join(sql_keys) + ") VALUES(" +
                           ",".join("?" for _ in sql_keys) + ")", tuple(payload.values()))
        if "campaigns" in actual:
            db.execute("DROP TABLE campaigns")
        db.execute("ALTER TABLE campaigns_new RENAME TO campaigns")
        db.execute("CREATE INDEX ix_campaigns_date ON campaigns(campaign_date,community_id,type_id)")
        db.execute("CREATE INDEX ix_campaigns_point_date ON campaigns(point_id,campaign_date)")
        db.execute("CREATE INDEX IF NOT EXISTS ix_points_temporary ON points(temporary,disabled)")

        db.execute("""
            CREATE TABLE photos_new (
                id TEXT PRIMARY KEY, inspection_id TEXT REFERENCES inspections(id) ON DELETE RESTRICT,
                issue_id TEXT REFERENCES issues(id) ON DELETE RESTRICT,
                action_id TEXT REFERENCES issue_actions(id) ON DELETE RESTRICT,
                point_id TEXT REFERENCES points(id) ON DELETE RESTRICT,
                campaign_id TEXT REFERENCES campaigns(id) ON DELETE RESTRICT,
                activity_id TEXT REFERENCES activities(id) ON DELETE RESTRICT,
                phase TEXT NOT NULL CHECK(phase IN ('site','after','review','point','before','process','activity')),
                relative_path TEXT NOT NULL UNIQUE, width INTEGER NOT NULL CHECK(width>0),
                height INTEGER NOT NULL CHECK(height>0), uploaded_at TEXT NOT NULL,
                has_thumb INTEGER NOT NULL DEFAULT 0 CHECK(has_thumb IN (0,1)),
                CHECK((inspection_id IS NOT NULL)+(issue_id IS NOT NULL)+(point_id IS NOT NULL)+
                      (campaign_id IS NOT NULL)+(activity_id IS NOT NULL)<=1),
                CHECK(action_id IS NULL OR issue_id IS NOT NULL),
                CHECK(inspection_id IS NULL OR phase='site'),
                CHECK(issue_id IS NULL OR phase IN ('site','after','review')),
                CHECK(point_id IS NULL OR phase='point'),
                CHECK(campaign_id IS NULL OR phase IN ('before','process','after')),
                CHECK(activity_id IS NULL OR phase='activity')
            )
        """)
        source_columns = columns(db, "photos")
        names = ["id", "inspection_id", "issue_id", "action_id", "point_id", "campaign_id",
                 "phase", "relative_path", "width", "height", "uploaded_at", "has_thumb"]
        sources = [col if col in source_columns else ("0" if col == "has_thumb" else "NULL") for col in names]
        db.execute("INSERT INTO photos_new(" + ",".join(names) + ") SELECT " + ",".join(sources) + " FROM photos")
        db.execute("DROP TABLE photos")
        db.execute("ALTER TABLE photos_new RENAME TO photos")
        for index, column in (("inspection", "inspection_id"), ("issue", "issue_id"), ("point", "point_id"),
                              ("campaign", "campaign_id"), ("activity", "activity_id")):
            db.execute(f"CREATE INDEX ix_photos_{index} ON photos({column})")
        db.execute("""
            CREATE INDEX ix_photos_unattached ON photos(uploaded_at)
            WHERE inspection_id IS NULL AND issue_id IS NULL AND point_id IS NULL
              AND campaign_id IS NULL AND activity_id IS NULL
        """)
        db.execute("PRAGMA user_version=1")
        problems = db.execute("PRAGMA foreign_key_check").fetchall()
        if problems:
            raise ValueError("迁移后外键关联有误：" + str(problems[:3]))
        if db.execute("PRAGMA integrity_check").fetchone()[0] != "ok":
            raise ValueError("迁移后数据库完整性检查失败")
        for table, expected in counts.items():
            if db.execute(f"SELECT COUNT(*) FROM {table}").fetchone()[0] != expected:
                if table == "points":  # legacy campaigns may create synthetic temporary points
                    continue
                raise ValueError(f"{table} 记录数不一致")
        db.commit()
    except Exception:
        db.rollback()
        raise
    finally:
        db.execute("PRAGMA foreign_keys=ON")
    return counts


def find_source_photos(root: pathlib.Path, db_path: pathlib.Path) -> pathlib.Path | None:
    candidates = (root / "photos", db_path.parent / "photos")
    for path in candidates:
        if path.is_dir():
            return path
    return None


def copy_photos(db: sqlite3.Connection, origin: pathlib.Path, target: pathlib.Path) -> int:
    rows = list(db.execute("SELECT id,relative_path,has_thumb FROM photos"))
    if not rows:
        return 0
    for photo_id, relative, has_thumb in rows:
        if not PHOTO_PATH.fullmatch(relative) or relative[7:39] != photo_id:
            raise ValueError("照片相对路径不符合规则：" + str(relative))
        for suffix, required in (("", True), (".thumb", bool(has_thumb))):
            if not required:
                continue
            filename = relative[:-4] + suffix + ".jpg"
            src = origin / filename
            if not src.is_file() or src.is_symlink():
                raise FileNotFoundError("照片缺失或使用了符号链接：" + str(src))
            dst = target / filename
            dst.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(src, dst)
    return len(rows)


def main() -> int:
    parser = argparse.ArgumentParser(description="旧版街道环保数据库独立迁移程序")
    parser.add_argument("--source", required=True, type=pathlib.Path, help="旧程序目录或 app.db")
    parser.add_argument("--output", required=True, type=pathlib.Path, help="不存在的新目录")
    args = parser.parse_args()
    source = args.source.resolve()
    root = source if source.is_dir() else source.parent.parent
    old_db = source if source.is_file() else source / "data" / "app.db"
    output = args.output.resolve()
    if not old_db.is_file():
        parser.error("未找到旧 data/app.db")
    if output.exists():
        parser.error("输出目录已存在，拒绝覆盖")
    if output == root or root in output.parents:
        parser.error("输出目录不能位于旧程序目录内部")
    staging = output.with_name(output.name + ".staging-" + uuid.uuid4().hex[:8])
    try:
        (staging / "data").mkdir(parents=True)
        (staging / "backup").mkdir()
        (staging / "photos").mkdir()
        (staging / "temp").mkdir()
        # SQLite backup API 包括已提交的 WAL 内容，旧库本身完全不写入。
        with sqlite3.connect("file:" + old_db.as_posix() + "?mode=ro", uri=True) as src:
            with sqlite3.connect(staging / "data" / "app.db") as dst:
                src.backup(dst)
        shutil.copy2(staging / "data" / "app.db", staging / "backup" / "before-convert.db")
        with sqlite3.connect(staging / "data" / "app.db") as dst:
            numbers = convert(dst)
            old_photos = find_source_photos(root, old_db)
            if numbers["photos"] and old_photos is None:
                raise FileNotFoundError("旧库有照片，但找不到 photos 目录")
            photo_count = copy_photos(dst, old_photos, staging / "photos") if old_photos else 0
            require_good(dst)
        staging.rename(output)
    except Exception:
        if staging.exists():
            shutil.rmtree(staging)
        raise
    print("转换完成：", output)
    print("旧版记录数：", numbers, "照片文件数：", photo_count)
    print("原目录未修改；转换前请关闭旧程序，启动新程序前先核对数据。")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (ValueError, FileNotFoundError, sqlite3.Error, OSError) as error:
        print("转换失败：" + str(error), file=sys.stderr)
        raise SystemExit(1)
