#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
启动本地预览服务器（只用 Python 标准库）。

用法：
    双击“启动预览.bat”
或：
    python start_preview.py

浏览器会自动打开 http://127.0.0.1:8765/preview.html
按 Ctrl+C 停止。
"""

import http.server
import os
import socket
import sys
import threading
import webbrowser
from functools import partial
from pathlib import Path

for _s in (sys.stdout, sys.stderr):
    try:
        _s.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass

ROOT = Path(__file__).resolve().parent
HOST = "127.0.0.1"
START_PORT = 8765


class Handler(http.server.SimpleHTTPRequestHandler):
    # SimpleHTTPRequestHandler 默认 HTTP/1.0，每个瓦片都会新建 TCP 连接。
    # HTTP/1.1 + Keep-Alive 可以让浏览器在 6 条连接上连续复用，预览明显更快。
    protocol_version = "HTTP/1.1"

    extensions_map = {
        **http.server.SimpleHTTPRequestHandler.extensions_map,
        ".webp": "image/webp",
        ".geojson": "application/geo+json",
        ".json": "application/json",
        ".js": "text/javascript",
        ".css": "text/css",
    }

    def end_headers(self):
        path = self.path.split("?", 1)[0].lower()
        if path.endswith((".html", ".geojson", ".json")):
            # 预览时不要缓存 HTML/GeoJSON，避免换了数据还看旧的
            self.send_header("Cache-Control", "no-store")
        elif path.endswith((".webp", ".png", ".jpg", ".jpeg", ".js", ".css")):
            # 瓦片和静态资源可以缓存；调试时强刷即可。
            self.send_header("Cache-Control", "public, max-age=86400")
        self.send_header("X-Content-Type-Options", "nosniff")
        super().end_headers()

    def log_message(self, fmt, *args):
        # 静默；需要排查时把下面一行取消注释
        # super().log_message(fmt, *args)
        pass


def pick_port() -> int:
    for port in range(START_PORT, START_PORT + 30):
        with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as s:
            s.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
            try:
                s.bind((HOST, port))
                return port
            except OSError:
                continue
    raise RuntimeError("8765–8794 端口都被占用，请先关闭其他预览服务。")


def main() -> int:
    if not (ROOT / "preview.html").exists():
        print(f"找不到 {ROOT / 'preview.html'}")
        return 1
    port = pick_port()
    url = f"http://{HOST}:{port}/preview.html"
    handler = partial(Handler, directory=str(ROOT))
    httpd = http.server.ThreadingHTTPServer((HOST, port), handler)
    httpd.daemon_threads = True

    print("=" * 60)
    print("桑珠孜区 · 2025 全国高质量一张图 · 本地预览")
    print(f"目录: {ROOT}")
    print(f"地址: {url}")
    print("按 Ctrl+C 停止")
    print("=" * 60)

    threading.Timer(0.8, lambda: webbrowser.open(url)).start()
    try:
        httpd.serve_forever()
    except KeyboardInterrupt:
        print("\n已停止。")
    finally:
        httpd.server_close()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
