#!/usr/bin/env python3
"""Exercise a copied NativeAOT executable using only Python's standard library.

The executable runs beside a new temporary data directory, from a different CWD.
The script never points the application at an existing or user-owned database.
"""

from __future__ import annotations

import argparse
import base64
import contextlib
import datetime as dt
import hashlib
import http.client
import io
import json
import os
from pathlib import Path
import shutil
import signal
import socket
import sqlite3
import subprocess
import sys
import tempfile
import time
import traceback
import urllib.error
import urllib.parse
import urllib.request
import zipfile


# A complete 8 x 4 JPEG, generated once for this test. No image package is needed
# on the machine running the integration tests.
JPEG = base64.b64decode(
    "/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAAgGBgcGBQgHBwcJCQgKDBQNDAsLDBkSEw8UHRofHh0aHBwgJC4nICIsIxwcKDcpLDAxNDQ0Hyc5PTgyPC4zNDL/"
    "2wBDAQkJCQwLDBgNDRgyIRwhMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjL/"
    "wAARCAAEAAgDASIAAhEBAxEB/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/"
    "8QAHwEAAwEBAQEBAQEBAQAAAAAAAAECAwQFBgcICQoL/8QAtREAAgECBAQDBAcFBAQAAQJ3AAECAxEEBSExBhJBUQdhcRMiMoEIFEKRobHBCSMzUvAVYnLRChYkNOEl8RcYGRomJygpKjU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6goOEhYaHiImKkpOUlZaXmJmaoqOkpaanqKmqsrO0tba3uLm6wsPExcbHyMnK0tPU1dbX2Nna4uPk5ebn6Onq8vP09fb3+Pn6/9oADAMBAAIRAxEAPwDGooorM+PP/9k="
)


class SmokeFailure(AssertionError):
    pass


def require(condition: bool, message: str) -> None:
    if not condition:
        raise SmokeFailure(message)


def ids(items: list[dict]) -> set[str]:
    return {item["id"] for item in items}


def day(value: dt.date) -> str:
    return value.isoformat()


def database_rows(path: Path) -> dict:
    with contextlib.closing(sqlite3.connect(path.as_uri() + "?mode=ro", uri=True)) as database:
        tables = [row[0] for row in database.execute("SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name")]
        # Names come from this test application's schema; quote identifiers rather
        # than treating them as SQL fragments. Values elsewhere are parameterized.
        return {
            table: database.execute('SELECT * FROM "' + table.replace('"', '""') + '" ORDER BY 1').fetchall()
            for table in tables
        }


class ApiSmoke:
    def __init__(self, executable: Path, work_root: Path | None, timeout: float):
        self.temp = Path(tempfile.mkdtemp(prefix="street-eco-api-smoke-", dir=work_root)).resolve()
        self.install = self.temp / "application"
        self.install.mkdir()
        self.executable = self.install / executable.name
        shutil.copy2(executable, self.executable)
        self.cwd = self.temp / "unrelated-working-directory"
        self.cwd.mkdir()
        self.data = self.install / "data"
        self.log_path = self.temp / "server.log"
        self.log = self.log_path.open("ab", buffering=0)
        self.process: subprocess.Popen | None = None
        self.opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
        self.timeout = timeout
        self.origin = ""
        self.report = {"platform": sys.platform, "executable": executable.name, "cases": [], "requests": [], "processes": [], "passed": False}
        public = self.install / "wwwroot"
        (public / "_nuxt").mkdir(parents=True)
        for filename in ("index.html", "200.html", "404.html"):
            (public / filename).write_text("<!doctype html><html lang=zh-CN><body>API smoke fixture</body></html>", encoding="utf-8")
        (public / "_nuxt" / "smoke.0123456789.js").write_text("console.log('local fixture');", encoding="utf-8")

    def start(self, seed: bool = False) -> None:
        require(self.process is None, "A previous test server is still running")
        with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as probe:
            probe.bind(("127.0.0.1", 0))
            port = probe.getsockname()[1]
        self.origin = f"http://127.0.0.1:{port}"
        command = [str(self.executable), "--no-browser", "--port", str(port)]
        if seed:
            command.append("--seed-demo")
        flags = subprocess.CREATE_NEW_PROCESS_GROUP if os.name == "nt" else 0
        self.process = subprocess.Popen(command, cwd=self.cwd, stdout=self.log, stderr=subprocess.STDOUT, creationflags=flags)
        deadline = time.monotonic() + self.timeout
        while time.monotonic() < deadline:
            if self.process.poll() is not None:
                raise SmokeFailure(f"Server exited during startup ({self.process.returncode}); see server.log")
            try:
                self.bootstrap = self.json("GET", "/api/bootstrap")
                self.today = dt.date.fromisoformat(self.bootstrap["today"])
                self.report["processes"].append({"pid": self.process.pid, "origin": self.origin, "seedDemo": seed})
                return
            except (urllib.error.URLError, TimeoutError, ConnectionError, OSError):
                time.sleep(0.1)
        raise SmokeFailure("Server did not become ready within the startup timeout")

    def stop(self) -> None:
        if self.process is None:
            return
        process = self.process
        try:
            if process.poll() is None:
                process.send_signal(signal.CTRL_BREAK_EVENT if os.name == "nt" else signal.SIGINT)
                process.wait(timeout=15)
            require(process.returncode == 0, f"Server did not shut down cleanly: {process.returncode}")
        finally:
            if process.poll() is None:
                process.kill()
                process.wait(timeout=5)
            self.process = None

    def request(self, method: str, path: str, body=None, *, raw: bytes | None = None, content_type="application/json", expected=(200,)):
        require(path.startswith("/") and not path.startswith("//"), "Test requests must remain on the temporary local server")
        require(body is None or raw is None, "A test request cannot have both JSON and raw bytes")
        data = raw if raw is not None else (json.dumps(body, ensure_ascii=False).encode("utf-8") if body is not None else None)
        request = urllib.request.Request(self.origin + path, data=data, method=method, headers={"Content-Type": content_type})
        started = time.monotonic()
        try:
            response = self.opener.open(request, timeout=20)
        except urllib.error.HTTPError as error:
            response = error
        with response:
            status, headers, payload = response.status, dict(response.headers.items()), response.read()
        self.report["requests"].append({"method": method, "path": path, "status": status, "ms": round((time.monotonic() - started) * 1000)})
        allowed = (expected,) if isinstance(expected, int) else expected
        if status not in allowed:
            raise SmokeFailure(f"{method} {path}: expected {allowed}, got {status}: {payload[:800].decode('utf-8', errors='replace')}")
        return headers, payload

    def json(self, method: str, path: str, body=None, **kwargs):
        _, payload = self.request(method, path, body, **kwargs)
        return json.loads(payload) if payload else None

    def case(self, name: str, action) -> None:
        started = time.monotonic()
        item = {"name": name, "passed": False}
        self.report["cases"].append(item)
        print(f"RUN {name}", flush=True)
        try:
            action()
            item["passed"] = True
            print(f"PASS {name}", flush=True)
        except Exception as error:
            item["failure"] = str(error)
            raise
        finally:
            item["seconds"] = round(time.monotonic() - started, 3)

    def fingerprint(self) -> dict:
        return {
            "database": database_rows(self.data / "app.db"),
            "photos": {path.relative_to(self.data / "photos").as_posix(): hashlib.sha256(path.read_bytes()).hexdigest()
                       for path in sorted((self.data / "photos").rglob("*")) if path.is_file()},
        }

    def sql_rows(self, statement: str, parameters=()) -> list:
        with contextlib.closing(sqlite3.connect(self.data / "app.db")) as database:
            return database.execute(statement, parameters).fetchall()

    def point_body(self, name: str, frequency="irregular", disabled=False) -> dict:
        return {"name": name, "communityId": self.community["id"], "typeId": self.point_type["id"],
                "address": "测试路 88 号", "contact": "测试联系人", "phone": "13800000000", "frequency": frequency,
                "notes": "仅供自动验收", "disabled": disabled}

    def create_point(self, name: str, frequency="irregular", disabled=False) -> dict:
        return self.json("POST", "/api/points", self.point_body(name, frequency, disabled), expected=201)

    def inspection_body(self, point_id: str, *, date: dt.date | None = None, issues=None, photo_ids=None) -> dict:
        return {"pointId": point_id, "date": day(date or self.today), "inspector": "测试排查人", "result": "issues" if issues else "normal",
                "notes": "手机排查自动验收", "noticeNumber": "测试编号 O'Brien 001", "issues": issues or [], "photoIds": photo_ids or []}

    def create_inspection(self, point_id: str, **kwargs) -> dict:
        return self.json("POST", "/api/inspections", self.inspection_body(point_id, **kwargs), expected=201)

    def issue_body(self, description: str, deadline: dt.date, photo_ids=None, responsible=None) -> dict:
        return {"description": description, "requirement": "清理并拍照反馈", "deadline": day(deadline), "photoIds": photo_ids or [], "responsibleUnit": responsible}

    def upload(self, phase="site") -> dict:
        photo = self.json("PUT", "/api/photos?" + urllib.parse.urlencode({"phase": phase}), raw=JPEG, content_type="image/jpeg", expected=(200, 201))
        require((photo["width"], photo["height"]) == (8, 4), "Server did not parse the JPEG SOF dimensions")
        require(bool(photo["id"]), "The server did not assign a photo ID")
        headers, payload = self.request("GET", "/photos/" + photo["id"])
        require(headers.get("Content-Type", "").startswith("image/jpeg") and payload == JPEG, "Photo response changed the stored JPEG bytes or media type")
        return photo

    def reject_oversized_photo(self) -> None:
        # Kestrel correctly rejects a declared oversized body before reading it.
        # urllib would still transmit 10 MB and can hit BrokenPipe before seeing
        # the 413 response. Expect/continue lets us inspect the rejection itself.
        origin = urllib.parse.urlsplit(self.origin)
        started = time.monotonic()
        with contextlib.closing(http.client.HTTPConnection(origin.hostname, origin.port, timeout=20)) as connection:
            connection.putrequest("PUT", "/api/photos?phase=site")
            connection.putheader("Content-Type", "image/jpeg")
            connection.putheader("Content-Length", str(10 * 1024 * 1024 + 1))
            connection.putheader("Expect", "100-continue")
            connection.endheaders()
            response = connection.getresponse()
            payload = response.read()
            self.report["requests"].append({"method": "PUT", "path": "/api/photos?phase=site", "status": response.status,
                                            "declaredBytes": 10 * 1024 * 1024 + 1, "ms": round((time.monotonic() - started) * 1000)})
            require(response.status == 413, "Oversized photo must be rejected before its body is transmitted: " + payload[:500].decode("utf-8", errors="replace"))

    def test_startup_and_settings(self) -> None:
        require((self.data / "app.db").is_file(), "Database was not created next to the copied executable")
        require(not (self.cwd / "data").exists(), "Database incorrectly depends on the current working directory")
        require(self.sql_rows("PRAGMA journal_mode")[0][0].lower() == "wal", "WAL mode is not enabled")
        require(self.sql_rows("PRAGMA user_version")[0][0] >= 1, "Schema migration was not recorded")
        require(self.sql_rows("PRAGMA application_id")[0][0] == 1397048143, "Database application identifier is missing")
        require({"餐饮油烟", "工地扬尘", "河道水体", "垃圾堆放", "小散乱污", "其他"}.issubset({row["name"] for row in self.bootstrap["pointTypes"]}), "Default point types are incomplete")
        self.settings = {"streetName": "测试街道 O'Brien", "issuingUnit": "生态环境办公室", "defaultInspector": "默认排查员",
                         "noticeTemplate": "经{日期}对{点位}排查：\n{问题}\n请于{期限}前完成整改。", "jpegQuality": 75}
        self.json("PUT", "/api/settings", self.settings)
        require(self.json("GET", "/api/settings") == self.settings, "Settings did not persist exact Unicode/template content")
        self.json("PUT", "/api/settings", {**self.settings, "jpegQuality": 101}, expected=400)
        require(self.json("GET", "/api/settings") == self.settings, "Invalid settings mutated valid data")
        for filename in ("/", "/index.html", "/200.html", "/404.html"):
            headers, payload = self.request("GET", filename)
            require(b"API smoke fixture" in payload, "Static files are not read from AppContext.BaseDirectory/wwwroot")
            require("no-cache" in headers.get("Cache-Control", "") or "no-store" in headers.get("Cache-Control", ""), "HTML must not be cached")
        headers, _ = self.request("GET", "/_nuxt/smoke.0123456789.js")
        require("immutable" in headers.get("Cache-Control", ""), "Hashed frontend resources must use immutable caching")
        self.json("GET", "/api/does-not-exist", expected=404)

    def test_dictionaries_and_points(self) -> None:
        self.community = self.json("POST", "/api/communities", {"name": "测试社区", "title": "测试社区居民委员会"}, expected=201)
        self.point_type = self.json("POST", "/api/point-types", {"name": "测试点位类别"}, expected=201)
        self.json("PUT", "/api/communities/" + self.community["id"], {"name": "测试社区修订", "title": "测试社区居民委员会修订"})
        self.community = next(row for row in self.json("GET", "/api/communities") if row["id"] == self.community["id"])
        self.json("PUT", "/api/point-types/" + self.point_type["id"], {"name": "测试点位类别修订"})
        self.point_type = next(row for row in self.json("GET", "/api/point-types") if row["id"] == self.point_type["id"])
        require(self.community["name"].endswith("修订") and self.point_type["name"].endswith("修订"), "Dictionary updates were not visible")
        self.point = self.create_point("测试点位 O'Brien")
        for path in ("/api/communities/" + self.community["id"], "/api/point-types/" + self.point_type["id"]):
            self.json("DELETE", path + "?confirm=true", expected=409)
        detail = self.json("GET", "/api/points/" + self.point["id"])
        require(detail["point"]["name"] == self.point["name"] and detail["inspections"] == [], "Point detail is inconsistent")
        query = urllib.parse.urlencode({"q": "O'Brien", "communityId": self.community["id"], "typeId": self.point_type["id"]})
        require(ids(self.json("GET", "/api/points?" + query)["items"]) == {self.point["id"]}, "Combined point filters failed")
        injection = urllib.parse.urlencode({"q": "' OR 1=1 --"})
        require(self.json("GET", "/api/points?" + injection)["items"] == [], "Search text was interpreted as SQL")
        body = self.point_body("测试点位停用", disabled=True)
        self.json("PUT", "/api/points/" + self.point["id"], body)
        require(self.point["id"] not in ids(self.json("GET", "/api/points")["items"]), "Disabled points leaked into the default list")
        require(self.point["id"] in ids(self.json("GET", "/api/points?disabled=true")["items"]), "Disabled filter omitted the point")
        self.json("POST", "/api/inspections", self.inspection_body(self.point["id"]), expected=409)
        self.json("PUT", "/api/points/" + self.point["id"], self.point_body("测试点位 O'Brien"))
        unused = self.create_point("测试可删除点位")
        self.json("DELETE", "/api/points/" + unused["id"], expected=400)
        self.json("DELETE", "/api/points/" + unused["id"] + "?confirm=true", expected=(200, 204))
        self.json("GET", "/api/points/" + unused["id"], expected=404)
        for collection, body in (("communities", {"name": "测试可删除社区", "title": "测试居委会"}), ("point-types", {"name": "测试可删除类别"})):
            item = self.json("POST", "/api/" + collection, body, expected=201)
            self.json("DELETE", "/api/" + collection + "/" + item["id"], expected=400)
            self.json("DELETE", "/api/" + collection + "/" + item["id"] + "?confirm=true", expected=(200, 204))
            require(item["id"] not in ids(self.json("GET", "/api/" + collection)), "Dictionary deletion did not persist")

    def test_inspections_and_photos(self) -> None:
        self.normal_photo = self.upload()
        self.normal = self.create_inspection(self.point["id"], photo_ids=[self.normal_photo["id"]])
        require(self.normal["inspection"]["result"] == "normal" and self.normal["issues"] == [], "A normal inspection must still produce a complete record")
        require(self.normal_photo["id"] in ids(self.normal["photos"]), "Inspection-level photo was not associated")
        self.json("DELETE", "/api/points/" + self.point["id"] + "?confirm=true", expected=409)
        self.json("PUT", "/api/photos?phase=site", raw=b"not-a-jpeg", content_type="image/jpeg", expected=400)
        self.json("PUT", "/api/photos?phase=unknown", raw=JPEG, content_type="image/jpeg", expected=400)
        self.reject_oversized_photo()
        before = self.fingerprint()
        self.json("POST", "/api/inspections", self.inspection_body(self.point["id"], date=self.today + dt.timedelta(days=1)), expected=400)
        empty_issues = {**self.inspection_body(self.point["id"]), "result": "issues"}
        self.json("POST", "/api/inspections", empty_issues, expected=400)
        invalid_normal = self.inspection_body(self.point["id"], issues=[self.issue_body("不应保存", self.today)])
        invalid_normal["result"] = "normal"
        self.json("POST", "/api/inspections", invalid_normal, expected=400)
        require(self.fingerprint() == before, "Rejected inspections left partial data")
        photos = [self.upload(), self.upload(), self.upload()]
        self.multi = self.create_inspection(self.point["id"], photo_ids=[photos[0]["id"]], issues=[
            self.issue_body("测试垃圾堆放", self.today - dt.timedelta(days=1), [photos[1]["id"]]),
            self.issue_body("测试油烟净化", self.today + dt.timedelta(days=2), [photos[2]["id"]], "明确责任单位")])
        require(self.multi["inspection"]["issueCount"] == 2 and len(self.multi["issues"]) == 2, "Multi-issue inspection did not commit all issues")
        require(self.multi["issues"][0]["responsibleUnit"] == self.community["name"], "Responsible unit did not default to the community")
        require(self.multi["issues"][1]["responsibleUnit"] == "明确责任单位", "Explicit responsible unit was lost")
        require({photo["id"] for photo in photos}.issubset(ids(self.multi["photos"])), "The inspection detail did not include its problem photos")
        for issue, photo in zip(self.multi["issues"], photos[1:]):
            require(photo["id"] in ids(self.json("GET", "/api/issues/" + issue["id"])["photos"]), "A problem photo was associated with the wrong issue")
        unattached = self.upload()
        before = self.fingerprint()
        partial = self.inspection_body(self.point["id"], issues=[self.issue_body("应回滚", self.today, [unattached["id"]]), self.issue_body("不存在照片", self.today, ["0" * 32])])
        self.json("POST", "/api/inspections", partial, expected=(400, 404, 409))
        require(self.fingerprint() == before, "An invalid later photo left an inspection, issue or earlier photo association committed")
        self.json("POST", "/api/inspections", self.inspection_body(self.point["id"], photo_ids=[self.normal_photo["id"]]), expected=(400, 409))
        require(self.fingerprint() == before, "Reusing an associated photo corrupted its first association")
        self.json("PUT", "/api/points/" + self.point["id"], self.point_body("测试点位已更名"))
        self.json("PUT", "/api/communities/" + self.community["id"], {"name": "测试社区再更名", "title": "测试社区新抬头"})
        historic = self.json("GET", "/api/inspections/" + self.multi["inspection"]["id"])["inspection"]
        require(historic["pointName"] == self.point["name"] and historic["communityTitle"] == self.community["title"], "Editing current dictionaries changed historical evidence")
        self.community = next(row for row in self.json("GET", "/api/communities") if row["id"] == self.community["id"])
        self.json("DELETE", "/api/photos/" + unattached["id"], expected=400)
        self.json("DELETE", "/api/photos/" + unattached["id"] + "?confirm=true", expected=(200, 204))
        self.request("GET", "/photos/" + unattached["id"], expected=404)

    def test_state_transitions(self) -> None:
        first, second = self.multi["issues"]
        prefix = "/api/issues/" + first["id"]
        self.json("POST", prefix + "/review", {"date": day(self.today), "conclusion": "不可跳过社区反馈"}, expected=409)
        after = self.upload("after")
        feedback = self.json("POST", prefix + "/feedback", {"date": day(self.today), "notes": "已清理完成", "photoIds": [after["id"]]})
        require(feedback["issue"]["status"] == "feedback" and feedback["issue"]["feedbackDate"] == day(self.today), "Feedback did not update status and date")
        require(after["id"] in ids(feedback["photos"]), "After photo was not associated")
        self.json("POST", prefix + "/feedback", {"date": day(self.today), "notes": "重复反馈"}, expected=409)
        self.json("PUT", prefix, self.issue_body("不能改已反馈问题", self.today, responsible=self.community["name"]), expected=409)
        before = self.fingerprint()
        self.json("POST", prefix + "/review", {"date": day(self.today - dt.timedelta(days=1)), "conclusion": "日期错误"}, expected=400)
        require(self.fingerprint() == before, "Invalid review date changed the issue")
        closed = self.json("POST", prefix + "/review", {"date": day(self.today), "conclusion": "现场复查整改到位", "photoIds": []})
        require(closed["issue"]["status"] == "closed" and closed["issue"]["closedDate"] == day(self.today), "Review without photos must close the issue")
        self.json("POST", prefix + "/review", {"date": day(self.today), "conclusion": "重复闭环"}, expected=409)
        returned = self.json("POST", prefix + "/return", {"reason": "需要补充清理"})
        require(returned["issue"]["status"] == "pending", "Return did not move the issue back to pending")
        for field in ("feedbackDate", "reviewDate", "closedDate"):
            require(returned["issue"][field] is None, "Return retained a stale current-round date: " + field)
        require(returned["issue"]["feedbackNotes"] == "" and returned["issue"]["reviewConclusion"] == "", "Return retained stale current-round text")
        require([action["kind"] for action in returned["actions"]] == ["feedback", "review", "return"], "Returning an issue lost audit history")
        require(after["id"] in ids(returned["photos"]), "Returning an issue deleted historical evidence photos")
        self.json("POST", prefix + "/return", {}, expected=409)
        prefix = "/api/issues/" + second["id"]
        wrong_phase = self.upload("site")
        before = self.fingerprint()
        self.json("POST", prefix + "/feedback", {"date": day(self.today), "notes": "错误相位", "photoIds": [wrong_phase["id"]]}, expected=(400, 409))
        require(self.fingerprint() == before, "A wrong-phase photo left an action or status update committed")
        after2, review_photo = self.upload("after"), self.upload("review")
        self.json("POST", prefix + "/feedback", {"date": day(self.today), "notes": "已落实", "photoIds": [after2["id"]]})
        closed2 = self.json("POST", prefix + "/review", {"date": day(self.today), "conclusion": "复查合格", "photoIds": [review_photo["id"]]})
        require(closed2["issue"]["status"] == "closed" and review_photo["id"] in ids(closed2["photos"]), "Review photo was not retained on closure")

    def test_dashboard(self) -> None:
        baseline = self.json("GET", "/api/dashboard")
        starts = {"weekly": self.today - dt.timedelta(days=self.today.weekday()), "monthly": self.today.replace(day=1),
                  "quarterly": self.today.replace(month=((self.today.month - 1) // 3) * 3 + 1, day=1)}
        due_points, satisfied_points = set(), set()
        for frequency, start in starts.items():
            missed = self.create_point("测试超周期-" + frequency, frequency)
            current = self.create_point("测试本周期-" + frequency, frequency)
            self.create_inspection(missed["id"], date=start - dt.timedelta(days=1))
            self.create_inspection(current["id"], date=start)
            due_points.add(missed["id"])
            satisfied_points.add(current["id"])
            count = self.json("GET", "/api/dashboard")["missedInspections"]["count"]
            require(count == baseline["missedInspections"]["count"] + len(due_points), frequency + " reminder did not distinguish previous and current calendar periods")
        never = self.create_point("测试从未排查", "weekly")
        disabled = self.create_point("测试停用提醒", "weekly", disabled=True)
        irregular = self.create_point("测试不定期提醒")
        due_points.add(never["id"])
        satisfied_points.update((disabled["id"], irregular["id"]))
        self.future_point = self.create_point("测试未来记录不充抵周期", "weekly")
        self.future_inspection = self.create_inspection(self.future_point["id"])
        satisfied_points.add(self.future_point["id"])
        deadline_point = self.create_point("测试期限边界")
        self.deadline_issues = {}
        for key, offset, status in (("overdue", -1, "pending"), ("today", 0, "pending"), ("third", 3, "pending"), ("fourth", 4, "pending"), ("feedback", -1, "feedback"), ("closed", -1, "closed")):
            issue = self.create_inspection(deadline_point["id"], issues=[self.issue_body("测试提醒-" + key, self.today + dt.timedelta(days=offset))])["issues"][0]
            if status in ("feedback", "closed"):
                self.json("POST", "/api/issues/" + issue["id"] + "/feedback", {"date": day(self.today), "notes": "测试反馈"})
            if status == "closed":
                self.json("POST", "/api/issues/" + issue["id"] + "/review", {"date": day(self.today), "conclusion": "测试闭环"})
            self.deadline_issues[key] = issue["id"]
        dashboard = self.json("GET", "/api/dashboard")
        require(dashboard["today"] == day(self.today), "Dashboard changed the local calendar date")
        for group in ("overdue", "dueSoon", "awaitingReview", "missedInspections"):
            require(dashboard[group]["count"] >= len(dashboard[group]["items"]) and len(dashboard[group]["items"]) <= 8, "Reminder counts disagree with the eight-item homepage preview")
        require(dashboard["missedInspections"]["count"] == baseline["missedInspections"]["count"] + len(due_points), "Missing-record, disabled or irregular inspection reminders are incorrect")
        require(not satisfied_points.intersection(ids(dashboard["missedInspections"]["items"])), "Current-period, disabled or irregular points generated false reminders")
        missed = self.json("GET", "/api/points?missed=true&pageSize=100")
        require(missed["total"] == dashboard["missedInspections"]["count"], "The missed-inspection list and homepage disagree")
        own_missed = self.json("GET", "/api/points?" + urllib.parse.urlencode({"missed": "true", "communityId": self.community["id"], "pageSize": 100}))
        require(ids(own_missed["items"]) == due_points, "The full reminder list omitted a scheduled point or included an ineligible one")
        fixture_ids = set(self.deadline_issues.values())
        # Homepage previews are capped at eight entries; complete counts and
        # filtered lists remain authoritative even when demo rows precede ours.
        for group, expected in (("overdue", {self.deadline_issues["overdue"], self.deadline_issues["feedback"]}),
                                ("dueSoon", {self.deadline_issues["today"], self.deadline_issues["third"]}),
                                ("awaitingReview", {self.deadline_issues["feedback"]})):
            require(dashboard[group]["count"] == baseline[group]["count"] + len(expected), group + " reminder count does not match deadline/status boundaries")
            require((ids(dashboard[group]["items"]) & fixture_ids).issubset(expected), group + " preview contains an ineligible issue")
        for query, expected in (({"overdue": "true"}, {self.deadline_issues["overdue"], self.deadline_issues["feedback"]}), ({"dueSoon": "true"}, {self.deadline_issues["today"], self.deadline_issues["third"]}), ({"status": "feedback"}, {self.deadline_issues["feedback"]})):
            query["communityId"] = self.community["id"]
            query["pageSize"] = 100
            actual = ids(self.json("GET", "/api/issues?" + urllib.parse.urlencode(query))["items"]) & fixture_ids
            require(actual == expected, "Issue list filters disagree with dashboard rules")

    def test_backups(self) -> None:
        overview = self.json("GET", "/api/backups")
        require(overview["retentionCount"] == 14, "Daily database backup retention is not 14")
        require(any(path.is_file() for path in (self.data / "backup").rglob("*.db")), "First startup did not create a daily database snapshot")
        baseline = self.fingerprint()
        headers, backup = self.request("GET", "/api/backups/download")
        require(headers.get("Content-Type", "").startswith("application/zip"), "Backup download has an unexpected content type")
        with zipfile.ZipFile(io.BytesIO(backup)) as archive:
            require("app.db" in archive.namelist(), "Backup ZIP has no database snapshot")
            self.backup_entries = {entry.filename: archive.read(entry) for entry in archive.infolist() if not entry.is_dir()}
        snapshot_path = self.temp / "exported-snapshot.db"
        snapshot_path.write_bytes(self.backup_entries["app.db"])
        with contextlib.closing(sqlite3.connect(snapshot_path)) as database:
            require(database.execute("PRAGMA integrity_check").fetchone()[0] == "ok", "VACUUM snapshot failed SQLite integrity checking")
            require(database.execute("PRAGMA foreign_key_check").fetchall() == [], "Backup contains broken foreign keys")
            photo_rows = database.execute("SELECT relative_path FROM photos").fetchall()
        require(database_rows(snapshot_path) == baseline["database"], "Backup snapshot differs from the committed live database")
        for (relative,) in photo_rows:
            key = "photos/" + relative.replace("\\", "/")
            require(key in self.backup_entries, "Backup ZIP omitted a registered photo")
            require(hashlib.sha256(self.backup_entries[key]).hexdigest() == baseline["photos"][relative], "Backup photo bytes differ from stored evidence")
        changed_settings = {**self.settings, "streetName": "恢复前的当前数据，应自动归档"}
        self.json("PUT", "/api/settings", changed_settings)
        extra = self.create_point("测试快照之后新增点位")
        self.upload()
        current = self.fingerprint()
        preview = self.json("PUT", "/api/backups/restore/prepare", raw=backup, content_type="application/zip")
        require(preview["pointCount"] == len(baseline["database"]["points"]) and preview["photoCount"] == len(photo_rows), "Restore preview counts are incorrect")
        self.json("POST", "/api/backups/restore", {"token": preview["token"]}, expected=400)
        require(self.fingerprint() == current, "Restore without a second confirmation changed current data")
        self.json("POST", "/api/backups/restore", {"token": "0" * 32, "confirm": True}, expected=(404, 410))
        for label, entries in (
            ("zip-slip", {**self.backup_entries, "../escaped-api-smoke.txt": b"must not be extracted"}),
            ("invalid-database", {**self.backup_entries, "app.db": b"not a sqlite database"}),
            ("missing-photo", {key: value for key, value in self.backup_entries.items() if key != "photos/" + photo_rows[0][0].replace("\\", "/")}),
        ):
            invalid = self.make_zip(entries)
            self.json("PUT", "/api/backups/restore/prepare", raw=invalid, content_type="application/zip", expected=(400, 409))
            require(self.fingerprint() == current, label + " validation changed current data")
        self.json("PUT", "/api/backups/restore/prepare", raw=b"PK\x03\x04truncated", content_type="application/zip", expected=400)
        require(not list(self.temp.rglob("escaped-api-smoke.txt")), "ZIP traversal materialized a file outside its allowed path")
        require(self.fingerprint() == current, "Corrupt ZIP changed current data")
        # Simulate a write failure at the archive destination, without changing
        # filesystem permissions or risking another application's directories.
        backup_dir, held = self.data / "backup", self.data / "backup-held-by-test"
        backup_dir.rename(held)
        backup_dir.write_text("intentionally blocked by integration test", encoding="utf-8")
        try:
            self.json("POST", "/api/backups/restore", {"token": preview["token"], "confirm": True}, expected=500)
            require(self.fingerprint() == current, "Failure to preserve old data damaged the live database or photos")
            require(self.json("GET", "/api/settings") == changed_settings, "Server connection did not survive the backup failure")
        finally:
            backup_dir.unlink()
            held.rename(backup_dir)
        preview = self.json("PUT", "/api/backups/restore/prepare", raw=backup, content_type="application/zip")
        restored = self.json("POST", "/api/backups/restore", {"token": preview["token"], "confirm": True})
        require(restored["ok"] is True, "Restore did not report completion")
        require(self.fingerprint() == baseline, "Successful restore did not restore exactly the selected database and photos")
        require(self.json("GET", "/api/settings") == self.settings, "Restored settings are not served by the reopened connection")
        self.json("GET", "/api/points/" + extra["id"], expected=404)
        preserved = (self.data / restored["preservedBackup"]).resolve()
        require(preserved.is_relative_to((self.data / "backup").resolve()), "Preserved backup path escaped the backup directory")
        require(database_rows(preserved / "app.db") == current["database"], "Pre-restore database was not preserved intact")
        preserved_photos = {path.relative_to(preserved / "photos").as_posix(): hashlib.sha256(path.read_bytes()).hexdigest()
                            for path in sorted((preserved / "photos").rglob("*")) if path.is_file()}
        require(preserved_photos == current["photos"], "Pre-restore photos were not preserved intact")

    @staticmethod
    def make_zip(entries: dict[str, bytes]) -> bytes:
        output = io.BytesIO()
        with zipfile.ZipFile(output, "w", compression=zipfile.ZIP_DEFLATED) as archive:
            for name, payload in entries.items():
                archive.writestr(name, payload)
        return output.getvalue()

    def test_cleanup_and_restart(self) -> None:
        old_orphan, recent_orphan = self.upload(), self.upload()
        missed_before = self.json("GET", "/api/dashboard")["missedInspections"]["count"]
        photo_paths = {row[0]: self.data / "photos" / row[1] for row in self.sql_rows("SELECT id,relative_path FROM photos WHERE id IN (?, ?, ?)", (old_orphan["id"], recent_orphan["id"], self.normal_photo["id"]))}
        self.stop()
        old_stamp = (dt.datetime.now(dt.timezone.utc) - dt.timedelta(days=8)).isoformat(timespec="seconds")
        recent_stamp = (dt.datetime.now(dt.timezone.utc) - dt.timedelta(days=6)).isoformat(timespec="seconds")
        with contextlib.closing(sqlite3.connect(self.data / "app.db")) as database:
            database.execute("UPDATE photos SET uploaded_at=? WHERE id=?", (old_stamp, old_orphan["id"]))
            database.execute("UPDATE photos SET uploaded_at=? WHERE id=?", (recent_stamp, recent_orphan["id"]))
            database.execute("UPDATE photos SET uploaded_at=? WHERE id=?", (old_stamp, self.normal_photo["id"]))
            database.execute("UPDATE inspections SET inspection_date=? WHERE id=?", (day(self.today + dt.timedelta(days=1)), self.future_inspection["inspection"]["id"]))
            database.commit()
        self.start(seed=True)
        self.request("GET", "/photos/" + old_orphan["id"], expected=404)
        require(not photo_paths[old_orphan["id"]].exists(), "Startup cleanup removed the orphan row but left its JPEG file")
        require(not self.sql_rows("SELECT id FROM photos WHERE id=?", (old_orphan["id"],)), "Old orphan row survived startup cleanup")
        self.request("GET", "/photos/" + recent_orphan["id"])
        self.request("GET", "/photos/" + self.normal_photo["id"])
        require(photo_paths[recent_orphan["id"]].exists(), "Cleanup removed an orphan younger than seven days")
        require(photo_paths[self.normal_photo["id"]].exists(), "Cleanup removed an old associated evidence photo")
        require(self.json("GET", "/api/settings") == self.settings, "Restart lost persisted settings or --seed-demo overwrote them")
        missed_after = self.json("GET", "/api/dashboard")["missedInspections"]["count"]
        require(missed_after == missed_before + 1, "A future-dated imported record incorrectly satisfied this week's inspection")
        require(self.json("GET", "/api/points/" + self.future_point["id"])["point"]["lastInspectionDate"] is None, "Point history incorrectly uses a future record as the latest completed inspection")
        before = self.fingerprint()
        self.stop()
        self.start(seed=True)
        require(self.fingerprint() == before, "Restart or repeated --seed-demo duplicated or changed existing data")
        self.stop()

    def test_interrupted_restore(self) -> None:
        require(self.process is None, "Restore-crash fixtures require the test server to be stopped")
        for committed in (False, True):
            before = self.fingerprint()
            backup_name = "before-restore-smoke-" + ("committed" if committed else "uncommitted")
            previous = self.data / "backup" / backup_name
            previous.mkdir()
            for suffix in ("", "-wal", "-shm"):
                source = self.data / ("app.db" + suffix)
                if source.exists():
                    source.rename(previous / source.name)
            (self.data / "photos").rename(previous / "photos")
            (self.data / "photos").mkdir()
            # These bytes came from the successful backup-download case above.
            # Construct the state just after new data was moved into place.
            for name, payload in self.backup_entries.items():
                if name == "app.db" or name.startswith("photos/"):
                    destination = self.data / name
                    destination.parent.mkdir(parents=True, exist_ok=True)
                    destination.write_bytes(payload)
            marker = "恢复已提交的新数据" if committed else "尚未提交，不应覆盖旧数据"
            with contextlib.closing(sqlite3.connect(self.data / "app.db")) as database:
                database.execute("UPDATE settings SET street_name=? WHERE id=1", (marker,))
                database.commit()
            replacement = self.fingerprint()
            journal = self.data / ".restore-journal.json"
            journal.write_text(json.dumps({"backupName": backup_name, "committed": committed}), encoding="utf-8")
            self.start(seed=True)
            require(self.fingerprint() == (replacement if committed else before), "Startup recovered the wrong side of an interrupted restore transaction")
            require(not journal.exists(), "Startup left a processed restore journal behind")
            self.request("GET", "/photos/" + self.normal_photo["id"])
            if committed:
                require(self.json("GET", "/api/settings")["streetName"] == marker, "A committed restore was rolled back")
                require(database_rows(previous / "app.db") == before["database"], "Crash recovery discarded the preserved old database")
                preserved_photos = {path.relative_to(previous / "photos").as_posix(): hashlib.sha256(path.read_bytes()).hexdigest()
                                    for path in sorted((previous / "photos").rglob("*")) if path.is_file()}
                require(preserved_photos == before["photos"], "Crash recovery discarded preserved old photos")
            else:
                require(self.json("GET", "/api/settings") == self.settings, "An uncommitted restore changed live settings")
            self.stop()

    def run(self) -> None:
        self.start(seed=True)
        for name, action in (
            ("startup, persistence location, settings and static-cache policy", self.test_startup_and_settings),
            ("dictionary and point CRUD, filters and reference protection", self.test_dictionaries_and_points),
            ("normal/multi-issue inspections, JPEG upload and transactional photo association", self.test_inspections_and_photos),
            ("feedback, optional-photo review, closure and return audit trail", self.test_state_transitions),
            ("calendar periods, deadline boundaries and dashboard/list agreement", self.test_dashboard),
            ("consistent backup, restore confirmation, invalid ZIP and failed-preservation safety", self.test_backups),
            ("orphan cleanup, future-record reminders, graceful restart and idempotent demo seed", self.test_cleanup_and_restart),
            ("restart recovery before and after restore commit, with old evidence retained", self.test_interrupted_restore),
        ):
            self.case(name, action)
        self.report["passed"] = True


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--executable", required=True, type=Path, help="Published NativeAOT StreetEcoTool executable (not a production data directory)")
    parser.add_argument("--report", type=Path, default=Path("tests/artifacts/api-smoke-report.json"))
    parser.add_argument("--work-root", type=Path, help="Optional parent for the fresh temporary application directory")
    parser.add_argument("--startup-timeout", type=float, default=30)
    parser.add_argument("--keep-temp", action="store_true", help="Keep only this test's temporary installation for troubleshooting")
    args = parser.parse_args()
    executable = args.executable.resolve()
    require(executable.is_file(), "The NativeAOT executable does not exist: " + str(executable))
    if args.work_root:
        args.work_root.mkdir(parents=True, exist_ok=True)
    report_path = args.report.resolve()
    report_path.parent.mkdir(parents=True, exist_ok=True)
    suite = ApiSmoke(executable, args.work_root, args.startup_timeout)
    result = 0
    try:
        suite.run()
    except Exception as error:
        result = 1
        suite.report["failure"] = str(error)
        suite.report["traceback"] = traceback.format_exc()
        print("FAIL " + str(error), file=sys.stderr, flush=True)
    finally:
        try:
            suite.stop()
        except Exception as error:
            result = 1
            suite.report["shutdownFailure"] = str(error)
        suite.log.close()
        shutil.copyfile(suite.log_path, report_path.with_suffix(".server.log"))
        suite.report["passed"] = result == 0
        if args.keep_temp:
            suite.report["temporaryDirectory"] = str(suite.temp)
        report_path.write_text(json.dumps(suite.report, ensure_ascii=False, indent=2), encoding="utf-8")
        if not args.keep_temp:
            shutil.rmtree(suite.temp)
    print(json.dumps({"passed": suite.report["passed"], "cases": len(suite.report["cases"]), "requests": len(suite.report["requests"]), "report": str(report_path)}, ensure_ascii=False), flush=True)
    return result


if __name__ == "__main__":
    raise SystemExit(main())
