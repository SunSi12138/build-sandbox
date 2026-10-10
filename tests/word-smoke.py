#!/usr/bin/env python3
"""Verify real NativeAOT Word endpoints and save sample documents for rendering.

Uses the isolated server harness and a complete JPEG fixture from api-smoke.py.
No production data, Office SDK, image package, or network service is needed.
"""
from __future__ import annotations

import argparse
import datetime as dt
import importlib.util
import io
import json
from pathlib import Path
import posixpath
import shutil
import sys
import traceback
import urllib.parse
import xml.etree.ElementTree as ET
import zipfile

spec = importlib.util.spec_from_file_location("api_smoke", Path(__file__).with_name("api-smoke.py"))
assert spec and spec.loader
api_smoke = importlib.util.module_from_spec(spec)
spec.loader.exec_module(api_smoke)
require = api_smoke.require
W = "{http://schemas.openxmlformats.org/wordprocessingml/2006/main}"
R = "{http://schemas.openxmlformats.org/officeDocument/2006/relationships}"
A = "{http://schemas.openxmlformats.org/drawingml/2006/main}"


def validate_docx(payload: bytes) -> tuple[str, int, ET.Element]:
    with zipfile.ZipFile(io.BytesIO(payload)) as archive:
        require(archive.testzip() is None, "DOCX ZIP has a failed CRC")
        names = archive.namelist()
        require(len(names) == len(set(names)), "Duplicate package part")
        parsed = {name: ET.fromstring(archive.read(name)) for name in names if name.endswith((".xml", ".rels"))}
        for required in ("[Content_Types].xml", "_rels/.rels", "word/document.xml", "word/styles.xml", "word/fontTable.xml", "word/footer1.xml", "word/_rels/document.xml.rels"):
            require(required in parsed, "Missing DOCX part: " + required)
        for name, root in parsed.items():
            if not name.endswith(".rels"):
                continue
            identifiers = [item.get("Id") for item in root]
            require(len(identifiers) == len(set(identifiers)), "Relationship IDs are not unique")
            base = "" if name == "_rels/.rels" else name.split("/_rels/")[0]
            for relationship in root:
                require(relationship.get("TargetMode") != "External", "An exported file depends on external resources")
                target = posixpath.normpath(posixpath.join(base, relationship.get("Target", "")))
                require(target in names, "Broken relationship: " + target)
        document = parsed["word/document.xml"]
        relationships = {item.get("Id"): item for item in parsed["word/_rels/document.xml.rels"]}
        for image in document.iter(A + "blip"):
            relation = relationships.get(image.get(R + "embed"))
            require(relation is not None and relation.get("Type", "").endswith("/image"), "Broken image relationship")
        media = [name for name in names if name.startswith("word/media/")]
        for name in media:
            image = archive.read(name)
            require(name.endswith(".jpg") and image.startswith(b"\xff\xd8") and image.endswith(b"\xff\xd9"), "A photo is not a complete JPEG")
        footer_text = "".join(parsed["word/footer1.xml"].itertext())
        require("PAGE" in footer_text, "Footer has no page number field")
        require(document.find(W + "body")[-1].tag == W + "sectPr", "Page section is not the final body child")
        return "\n".join(element.text or "" for element in document.iter(W + "t")), len(media), document


class WordSmoke(api_smoke.ApiSmoke):
    def __init__(self, *args, output: Path, **kwargs):
        super().__init__(*args, **kwargs)
        self.output = output
        output.mkdir(parents=True, exist_ok=True)

    def download(self, route: str, name: str):
        headers, payload = self.request("GET", route)
        require(headers.get("Content-Type", "").startswith("application/vnd.openxmlformats-officedocument.wordprocessingml.document"), "Wrong Word content type")
        disposition = headers.get("Content-Disposition", "")
        require("attachment" in disposition and ".docx" in disposition, "Word response is not a named download")
        result = validate_docx(payload)
        (self.output / name).write_bytes(payload)
        self.report.setdefault("documents", []).append({"name": name, "bytes": len(payload), "photos": result[1], "disposition": disposition})
        return result

    def fixtures(self):
        self.community = self.json("POST", "/api/communities", {"name": "文档验收社区", "title": "文档验收社区居民委员会"}, expected=201)
        self.point_type = self.bootstrap["pointTypes"][0]
        self.point = self.create_point('河岸示例点位（合成数据）')
        self.settings = {**self.bootstrap["settings"], "streetName": "版式验收街道办事处", "issuingUnit": "版式验收街道办事处", "defaultInspector": "验收人员"}
        self.json("PUT", "/api/settings", self.settings)
        shared, first, second = self.upload(), self.upload(), self.upload()
        body = self.inspection_body(self.point["id"], date=self.today - dt.timedelta(days=2), photo_ids=[shared["id"]], issues=[
            self.issue_body("岸边生活垃圾未及时清理。含 XML 特殊字符：A&B <现场>。", self.today - dt.timedelta(days=1), [first["id"]]),
            self.issue_body("围挡外裸土覆盖不完整，需补充覆盖并加强巡查。", self.today + dt.timedelta(days=2), [second["id"]]),
        ])
        body["noticeNumber"] = "手填验收〔2026〕01号"
        self.inspection = self.json("POST", "/api/inspections", body, expected=201)
        self.inspection_id = self.inspection["inspection"]["id"]
        first_issue = self.inspection["issues"][0]["id"]
        after, review = self.upload("after"), self.upload("review")
        self.json("POST", f"/api/issues/{first_issue}/feedback", {"date": self.today.isoformat(), "notes": "社区已完成清理，安排专人维护。", "photoIds": [after["id"]]})
        self.json("POST", f"/api/issues/{first_issue}/review", {"date": self.today.isoformat(), "conclusion": "现场已清理，复查闭环。", "photoIds": [review["id"]]})
        self.normal = self.create_inspection(self.point["id"])
        # Historical inspection metadata must survive subsequent point edits.
        self.json("PUT", "/api/points/" + self.point["id"], self.point_body("后续修改的点位名称"))

    def single_inspection(self):
        text, photos, document = self.download(f"/api/inspections/{self.inspection_id}/export/record", "record.docx")
        require(photos == 3, "Record export must include only the three site photos")
        require("河岸示例点位（合成数据）" in text and "后续修改的点位名称" not in text, "Historical point snapshot was lost")
        require("A&B <现场>" in text, "XML escaping corrupted input text")
        size = document.find(".//" + W + "pgSz")
        require((size.get(W + "w"), size.get(W + "h")) == ("11906", "16838"), "Record must use portrait A4")
        text, photos, _ = self.download(f"/api/inspections/{self.inspection_id}/export/notice", "notice.docx")
        require(photos == 3 and "手填验收〔2026〕01号" in text and "居民委员会：" in text, "Notice metadata or photos missing")
        require(all(issue["description"] in text and issue["deadline"] in text for issue in self.inspection["issues"]), "Notice omitted an issue or deadline")
        require("整改情况" in text and "负责人签字" in text and "盖章" in text, "Notice receipt is incomplete")
        text, photos, _ = self.download(f"/api/inspections/{self.normal['inspection']['id']}/export/record", "normal-record.docx")
        require(photos == 0 and "正常" in text, "A normal inspection did not produce a record")
        self.request("GET", f"/api/inspections/{self.normal['inspection']['id']}/export/notice", expected=400)
        self.request("GET", "/api/inspections/not-found/export/record", expected=404)

    def ledger(self):
        query = urllib.parse.urlencode({"from": (self.today - dt.timedelta(days=3)).isoformat(), "to": self.today.isoformat(), "communityId": self.community["id"]})
        self.ledger_route = "/api/exports/ledger?" + query
        text, photos, document = self.download(self.ledger_route + "&photos=true", "ledger-photos.docx")
        require(photos == 5 and "复查" in text and "整改后" in text, "Ledger before/after/review photos are incomplete or duplicated")
        require("共 2 条，已闭环 1，未闭环 1，逾期 0。" in text, "Ledger summary is wrong")
        size = document.find(".//" + W + "pgSz")
        require(size.get(W + "orient") == "landscape" and size.get(W + "w") == "16838", "Ledger must be landscape A4")
        _, photos, _ = self.download(self.ledger_route + "&photos=false", "ledger-no-photos.docx")
        require(photos == 0, "No-photo ledger unexpectedly embedded images")
        self.request("GET", "/api/exports/ledger?from=invalid&to=2026-10-10", expected=400)
        self.request("GET", "/api/exports/ledger?from=2026-12-31&to=2026-01-01", expected=400)
        self.request("GET", self.ledger_route.replace(self.community["id"], "not-found"), expected=400)

    def long_text(self):
        description = "逐段核验：围挡内堆放材料，需覆盖裸露区域并清运垃圾。" * 140
        self.create_inspection(self.point["id"], issues=[self.issue_body(description, self.today - dt.timedelta(days=1))])
        text, _, _ = self.download(self.ledger_route, "ledger-long-text.docx")
        require(description in text, "Long issue description was truncated in Word export")
        require("共 3 条，已闭环 1，未闭环 2，逾期 1。" in text, "Long ledger summary is wrong")
        require(not list((self.data / ".temporary").glob("*.docx")), "Completed downloads left temporary DOCX files")

    def run(self):
        self.start()
        self.case("Word fixtures, closed issue and historical point snapshot", self.fixtures)
        self.case("record, notice, normal result, photo phases and download headers", self.single_inspection)
        self.case("landscape ledger, comparison photos, exact summary and invalid filters", self.ledger)
        self.case("long text is retained and downloaded temporary files are removed", self.long_text)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--executable", required=True, type=Path)
    parser.add_argument("--report", type=Path, default=Path("test-results/word-smoke-report.json"))
    parser.add_argument("--work-root", type=Path)
    args = parser.parse_args()
    report = args.report.resolve()
    report.parent.mkdir(parents=True, exist_ok=True)
    suite = WordSmoke(args.executable.resolve(), args.work_root, 30, output=report.parent / "word-samples")
    result = 0
    try:
        suite.run()
    except Exception as error:
        result = 1
        suite.report.update(failure=str(error), traceback=traceback.format_exc())
        print("FAIL " + str(error), file=sys.stderr, flush=True)
    finally:
        try:
            suite.stop()
        except Exception as error:
            result = 1
            suite.report["shutdownFailure"] = str(error)
        suite.log.close()
        shutil.copyfile(suite.log_path, report.with_suffix(".server.log"))
        suite.report["passed"] = result == 0
        report.write_text(json.dumps(suite.report, ensure_ascii=False, indent=2), encoding="utf-8")
        shutil.rmtree(suite.temp)
    print(json.dumps({"passed": result == 0, "cases": len(suite.report["cases"]), "requests": len(suite.report["requests"]), "report": str(report)}))
    return result


if __name__ == "__main__":
    raise SystemExit(main())
