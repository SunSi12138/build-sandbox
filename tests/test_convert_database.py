"""Offline conversion regression tests using schemas preserved from released code."""
from contextlib import closing
from pathlib import Path
import hashlib
import re
import sqlite3
import subprocess
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parent.parent
SCRIPT = ROOT / 'scripts/convert_database.py'
PHOTO = 'a' * 32


def fingerprint(root):
    return {str(p.relative_to(root)): hashlib.sha256(p.read_bytes()).hexdigest()
            for p in root.rglob('*') if p.is_file()}


class ConversionTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.source = self.root / 'old # database'
        (self.source / 'data').mkdir(parents=True)
        self.db = self.source / 'data/app.db'
        self.output = self.root / 'new'

    def seed(self, version, photos=False):
        with closing(sqlite3.connect(self.db)) as db:
            db.executescript((ROOT / f'tests/fixtures/legacy-schema-{version}.sql').read_text(encoding="utf-8"))
            db.executescript("""
                INSERT INTO settings VALUES(1,'街道','单位','人员','模板',75);
                INSERT INTO communities VALUES('c','社区','抬头');
                INSERT INTO point_types VALUES('t','点位类型');
                INSERT INTO points(id,name,community_id,type_id,address,frequency) VALUES('p','保留点位','c','t','原地址','weekly');
                INSERT INTO inspections VALUES('i','p','2026-01-01','人员','issues','','','保留点位','c','社区','抬头','t','点位类型','原地址','','');
                INSERT INTO issues(id,inspection_id,description,deadline,responsible_unit) VALUES('j','i','旧问题原文','2026-01-02','责任单位');
                INSERT INTO issue_actions VALUES('a','j','feedback','2026-01-02','历史反馈');
            """)
            if version >= 2:
                db.execute("INSERT INTO campaign_types VALUES('ct','清理')")
                keys = 'id,name,type_id,type_name,community_id,community_name,location,campaign_date,content'
                values = ['k','历史整治','ct','清理','c','社区','原地点','2026-01-02','原开展情况']
                if version == 3:
                    keys += ',point_id,point_name'
                    values += ['p','保留点位']
                db.execute(f"INSERT INTO campaigns({keys}) VALUES({','.join('?' for _ in values)})", values)
            if photos:
                db.execute("INSERT INTO photos(id,issue_id,phase,relative_path,width,height,uploaded_at) VALUES(?,'j','site',?,8,4,'2026-01-01T12:00:00')", (PHOTO, f'202601/{PHOTO}.jpg'))
                photo_root = self.source / ('data/photos' if version == 1 else 'photos') / '202601'
                photo_root.mkdir(parents=True)
                (photo_root / f'{PHOTO}.jpg').write_bytes(b'photo-bytes-preserved')
            db.commit()
        (self.source / 'appsettings.json').write_text('{"port":5181}', encoding="utf-8")

    def run_conversion(self, success=True, source=None):
        before = fingerprint(self.source)
        result = subprocess.run([sys.executable, str(SCRIPT), '--source', str(source or self.source), '--output', str(self.output)], capture_output=True, text=True)
        self.assertEqual(result.returncode == 0, success, result.stderr)
        self.assertEqual(fingerprint(self.source), before)
        self.assertFalse(list(self.root.glob('new.staging-*')))
        return result

    def test_legacy_versions_preserve_data_and_match_current_schema(self):
        # Each version gets an independent directory and original schema.
        for version in (1, 2, 3):
            with self.subTest(version=version), tempfile.TemporaryDirectory() as temp:
                original = self.root, self.source, self.db, self.output
                self.root = Path(temp); self.source = self.root / 'old # data'
                (self.source / 'data').mkdir(parents=True)
                self.db = self.source / 'data/app.db'; self.output = self.root / 'new'
                self.seed(version, photos=True)
                self.run_conversion(source=self.db)
                with closing(sqlite3.connect(self.output / 'data/app.db')) as db:
                    self.assertEqual(db.execute('PRAGMA user_version').fetchone(), (1,))
                    self.assertEqual(db.execute('PRAGMA foreign_key_check').fetchall(), [])
                    self.assertEqual(db.execute('SELECT name,address FROM points WHERE id="p"').fetchone(), ('保留点位','原地址'))
                    self.assertEqual(db.execute('SELECT description,issue_type_name FROM issues').fetchone(), ('旧问题原文','其他'))
                    self.assertEqual(db.execute('SELECT notes FROM issue_actions').fetchone(), ('历史反馈',))
                    self.assertEqual(db.execute('SELECT count(*) FROM campaigns').fetchone(), (int(version >= 2),))
                    if version >= 2:
                        self.assertEqual(db.execute('SELECT content FROM campaigns').fetchone(), ('原开展情况',))
                        self.assertEqual(db.execute('SELECT count(*) FROM campaigns JOIN points ON campaigns.point_id=points.id').fetchone(), (1,))
                    sql = re.search(r'Command\("""\n(.*?)""", transaction\)', (ROOT / 'server/Database.cs').read_text(encoding="utf-8"), re.S).group(1)
                    with closing(sqlite3.connect(':memory:')) as fresh:
                        fresh.executescript(sql)
                        for (table,) in fresh.execute("SELECT name FROM sqlite_master WHERE type='table'"):
                            self.assertEqual({r[1] for r in db.execute(f'PRAGMA table_info({table})')}, {r[1] for r in fresh.execute(f'PRAGMA table_info({table})')}, table)
                        self.assertEqual({r[0] for r in db.execute("SELECT name FROM sqlite_master WHERE type='index'")}, {r[0] for r in fresh.execute("SELECT name FROM sqlite_master WHERE type='index'")})
                self.assertEqual((self.output / f'photos/202601/{PHOTO}.jpg').read_bytes(), b'photo-bytes-preserved')
                self.assertEqual((self.output / 'appsettings.json').read_text(encoding="utf-8"), '{"port":5181}')
                self.assertTrue((self.output / 'backup/before-convert.db').is_file())
                self.root, self.source, self.db, self.output = original

    def test_missing_photo_removes_output_and_preserves_source(self):
        self.seed(3, photos=True)
        (self.source / f'photos/202601/{PHOTO}.jpg').unlink()
        self.run_conversion(False)
        self.assertFalse(self.output.exists())

    def test_existing_output_is_not_overwritten(self):
        self.seed(3)
        self.output.mkdir(); (self.output / 'keep').write_text('keep')
        self.run_conversion(False)
        self.assertEqual((self.output / 'keep').read_text(encoding="utf-8"), 'keep')

    def test_repeated_conversion_is_refused(self):
        self.seed(3); self.run_conversion()
        self.source = self.output; self.output = self.root / 'again'
        self.run_conversion(False)
        self.assertFalse(self.output.exists())

    def test_future_version_is_refused(self):
        self.seed(3)
        with closing(sqlite3.connect(self.db)) as db:
            db.execute('PRAGMA user_version=99')
        self.run_conversion(False)
        self.assertFalse(self.output.exists())

    def test_wal_commits_are_copied_without_touching_source(self):
        self.seed(3)
        with closing(sqlite3.connect(self.db)) as db:
            db.execute('PRAGMA journal_mode=WAL')
            db.execute("UPDATE points SET name='WAL点位' WHERE id='p'")
            db.commit()
            self.run_conversion()
            with closing(sqlite3.connect(self.output / 'data/app.db')) as result:
                self.assertEqual(result.execute('SELECT name FROM points').fetchone(), ('WAL点位',))


if __name__ == '__main__':
    unittest.main()
