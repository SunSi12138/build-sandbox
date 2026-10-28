#!/usr/bin/env python3
"""Check map settings, cache reuse and forced updates with an isolated local tile server."""
from __future__ import annotations
import argparse
import importlib.util
import json
from pathlib import Path
import shutil
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

spec = importlib.util.spec_from_file_location("api_smoke", Path(__file__).with_name("api-smoke.py"))
api_smoke = importlib.util.module_from_spec(spec)
spec.loader.exec_module(api_smoke)
require = api_smoke.require


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--executable", type=Path, required=True)
    parser.add_argument("--report", type=Path)
    args = parser.parse_args()
    state = {"calls": [], "body": api_smoke.JPEG, "fail": False}

    class Tiles(BaseHTTPRequestHandler):
        def do_GET(self):
            state["calls"].append((self.path, self.headers.get("Cache-Control")))
            self.send_response(503 if state["fail"] else 200)
            self.end_headers()
            if not state["fail"]:
                self.wfile.write(state["body"])
        def log_message(self, *args):
            pass

    upstream = ThreadingHTTPServer(("127.0.0.1", 0), Tiles)
    thread = threading.Thread(target=upstream.serve_forever, daemon=True)
    thread.start()
    app = api_smoke.ApiSmoke(args.executable.resolve(), None, 20)
    try:
        app.start()
        settings = app.json("GET", "/api/map/settings")
        require(len(settings["providers"]) == 1, "A new database should have exactly one layer")
        source = settings["providers"][0]
        layer_id = source["id"]
        source.update(name="测试卫星图", folder="custom", scheme="xyz", urlTemplate=f"http://127.0.0.1:{upstream.server_port}/{{z}}/{{x}}/{{y}}?key={{key}}", apiKey="test-key")
        app.json("PUT", f"/api/map/settings/{layer_id}", source)
        headers, first = app.request("GET", f"/api/map/tiles/{layer_id}/2/1/0")
        require(first == api_smoke.JPEG and len(state["calls"]) == 1, "First tile was not downloaded")
        require(state["calls"][-1][0] == "/2/1/0?key=test-key", "XYZ coordinates or key substitution differ")
        require(headers.get("Cache-Control") == "no-store", "Browser cache must not override map settings")
        require((app.install / "tiles/custom/2/1/0.jpg").is_file(), "Relative cache directory was ignored")
        app.request("GET", f"/api/map/tiles/{layer_id}/2/1/0")
        require(len(state["calls"]) == 1, "Cached tile was unnecessarily downloaded")
        source["forceRefresh"] = True
        app.json("PUT", f"/api/map/settings/{layer_id}", source)
        state["body"] = api_smoke.JPEG + b"updated"
        _, updated = app.request("GET", f"/api/map/tiles/{layer_id}/2/1/0")
        require(updated == state["body"] and len(state["calls"]) == 2, "Forced update did not replace the cache")
        require(state["calls"][-1][1] == "no-cache", "Forced request did not ask upstream to revalidate")
        state["fail"] = True
        _, fallback = app.request("GET", f"/api/map/tiles/{layer_id}/2/1/0")
        require(fallback == updated and len(state["calls"]) == 3, "Unavailable upstream did not retain the cache")
        state["fail"] = False
        source.update(folder=str(app.temp / "external-cache"), scheme="tms", forceRefresh=False)
        app.json("PUT", f"/api/map/settings/{layer_id}", source)
        app.request("GET", f"/api/map/tiles/{layer_id}/2/1/0")
        require((app.temp / "external-cache/2/1/3.jpg").is_file(), "Absolute directory or TMS row was ignored")
        require(state["calls"][-1][0] == "/2/1/3?key=test-key", "TMS coordinates differ")
        extra = app.json("POST", "/api/map/settings", {**source, "name": "标注层", "folder": "labels", "urlTemplate": "", "apiKey": "", "forceRefresh": False}, expected=201)
        app.json("PUT", "/api/map/settings/order", {"ids": [extra["id"], layer_id]})
        require([item["id"] for item in app.json("GET", "/api/map/layers")] == [layer_id, extra["id"]], "Layer drawing order differs")
        app.json("PUT", f"/api/map/settings/{extra['id']}/enabled", {"enabled": False})
        require([item["id"] for item in app.json("GET", "/api/map/layers")] == [layer_id], "Disabled layer is still rendered")
        source["sortOrder"] = 1
        app.stop(); app.start()
        persisted = app.json("GET", "/api/map/settings")
        require(next(item for item in persisted["providers"] if item["id"] == layer_id) == source, "Saved map settings did not survive restart")
        app.report["passed"] = True
        print("PASS map settings, live updates, XYZ/TMS, relative/absolute cache, forced update and offline fallback")
    finally:
        app.stop(); app.log.close(); upstream.shutdown(); upstream.server_close()
        if args.report:
            args.report.parent.mkdir(parents=True, exist_ok=True)
            args.report.write_text(json.dumps(app.report, ensure_ascii=False, indent=2), encoding="utf-8")
        shutil.rmtree(app.temp)


if __name__ == "__main__":
    main()
