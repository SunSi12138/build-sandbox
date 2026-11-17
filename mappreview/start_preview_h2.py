#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
HTTP/2 本地预览服务（可选，和 8765 的 HTTP/1.1 预览并存）。

特点：
  * TLS + ALPN，浏览器用 HTTP/2 多路复用加载瓦片
  * 首次运行自动生成本地自签名证书 certs/localhost.crt / localhost.key
  * 静态文件、缓存头、MIME 类型与 start_preview.py 一致

用法：
    python start_preview_h2.py
    # 浏览器打开 https://127.0.0.1:8443/preview.html
    # 自签名证书会提示不安全，选择“高级 → 继续访问”

依赖：
    pip install hypercorn cryptography

如果想把证书加入当前 Windows 用户信任库（可选，需要确认）：
    certutil -user -addstore Root certs\\localhost.crt
"""

import asyncio
import mimetypes
import socket
import sys
from datetime import datetime, timedelta, timezone
from pathlib import Path
from urllib.parse import unquote

for _s in (sys.stdout, sys.stderr):
    try:
        _s.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass

ROOT = Path(__file__).resolve().parent
CERT_DIR = ROOT / "certs"
CERT_FILE = CERT_DIR / "localhost.crt"
KEY_FILE = CERT_DIR / "localhost.key"
HOST = "127.0.0.1"
START_PORT = 8443

MIME = {
    ".html": "text/html; charset=utf-8",
    ".js": "text/javascript; charset=utf-8",
    ".css": "text/css; charset=utf-8",
    ".json": "application/json; charset=utf-8",
    ".geojson": "application/geo+json; charset=utf-8",
    ".webp": "image/webp",
    ".png": "image/png",
    ".jpg": "image/jpeg",
    ".jpeg": "image/jpeg",
    ".txt": "text/plain; charset=utf-8",
}


def make_self_signed_cert() -> None:
    """生成本地 localhost/127.0.0.1 自签名证书。"""
    try:
        from cryptography import x509
        from cryptography.hazmat.primitives import hashes, serialization
        from cryptography.hazmat.primitives.asymmetric import rsa
        from cryptography.x509.oid import NameOID
        from ipaddress import ip_address
    except Exception as exc:
        raise SystemExit(
            "缺少 cryptography，请先执行：pip install hypercorn cryptography\n"
            f"原始错误：{exc}"
        )

    CERT_DIR.mkdir(parents=True, exist_ok=True)
    key = rsa.generate_private_key(public_exponent=65537, key_size=2048)
    name = x509.Name([
        x509.NameAttribute(NameOID.COMMON_NAME, "localhost"),
        x509.NameAttribute(NameOID.ORGANIZATION_NAME, "Local Preview"),
    ])
    now = datetime.now(timezone.utc)
    cert = (
        x509.CertificateBuilder()
        .subject_name(name)
        .issuer_name(name)
        .public_key(key.public_key())
        .serial_number(x509.random_serial_number())
        .not_valid_before(now - timedelta(days=1))
        .not_valid_after(now + timedelta(days=825))
        .add_extension(
            x509.SubjectAlternativeName([
                x509.DNSName("localhost"),
                x509.IPAddress(ip_address("127.0.0.1")),
            ]),
            critical=False,
        )
        .add_extension(x509.BasicConstraints(ca=True, path_length=None), critical=True)
        .sign(key, hashes.SHA256())
    )
    KEY_FILE.write_bytes(key.private_bytes(
        encoding=serialization.Encoding.PEM,
        format=serialization.PrivateFormat.TraditionalOpenSSL,
        encryption_algorithm=serialization.NoEncryption(),
    ))
    CERT_FILE.write_bytes(cert.public_bytes(serialization.Encoding.PEM))
    print(f"已生成本地证书：{CERT_FILE}")
    print(f"私钥：{KEY_FILE}")


def pick_port() -> int:
    for port in range(START_PORT, START_PORT + 30):
        with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as s:
            s.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
            try:
                s.bind((HOST, port))
                return port
            except OSError:
                continue
    raise RuntimeError("8443–8472 端口都被占用")


def content_type(path: Path) -> str:
    suffix = path.suffix.lower()
    if suffix in MIME:
        return MIME[suffix]
    return mimetypes.guess_type(str(path))[0] or "application/octet-stream"


async def app(scope, receive, send):
    """极简 ASGI 静态文件服务。"""
    if scope["type"] == "lifespan":
        while True:
            message = await receive()
            if message["type"] == "lifespan.startup":
                await send({"type": "lifespan.startup.complete"})
            elif message["type"] == "lifespan.shutdown":
                await send({"type": "lifespan.shutdown.complete"})
                return
        return

    if scope["type"] != "http":
        return

    raw_path = unquote(scope.get("path", "/"))
    rel = raw_path.lstrip("/")
    if not rel or rel.endswith("/"):
        rel += "preview.html"
    target = (ROOT / rel).resolve()
    try:
        target.relative_to(ROOT)
    except ValueError:
        await _send_text(send, 403, "Forbidden")
        return

    if not target.is_file():
        await _send_text(send, 404, "Not Found")
        return

    try:
        body = await asyncio.to_thread(target.read_bytes)
    except OSError:
        await _send_text(send, 500, "Read Error")
        return

    suffix = target.suffix.lower()
    if suffix in (".html", ".geojson", ".json"):
        cache = "no-store"
    else:
        cache = "public, max-age=86400"
    headers = [
        (b"content-type", content_type(target).encode("latin-1")),
        (b"content-length", str(len(body)).encode("ascii")),
        (b"cache-control", cache.encode("ascii")),
        (b"x-content-type-options", b"nosniff"),
    ]
    await send({"type": "http.response.start", "status": 200, "headers": headers})
    await send({"type": "http.response.body", "body": body})


async def _send_text(send, status: int, text: str) -> None:
    body = text.encode("utf-8")
    await send({
        "type": "http.response.start",
        "status": status,
        "headers": [
            (b"content-type", b"text/plain; charset=utf-8"),
            (b"content-length", str(len(body)).encode("ascii")),
            (b"cache-control", b"no-store"),
        ],
    })
    await send({"type": "http.response.body", "body": body})


def main() -> int:
    if not (ROOT / "preview.html").exists():
        print(f"找不到 {ROOT / 'preview.html'}")
        return 1
    try:
        import hypercorn  # noqa: F401
        from hypercorn.asyncio import serve
        from hypercorn.config import Config
    except Exception as exc:
        print("缺少 hypercorn，请先执行：pip install hypercorn cryptography")
        print(f"原始错误：{exc}")
        return 1

    if not CERT_FILE.exists() or not KEY_FILE.exists():
        make_self_signed_cert()

    port = pick_port()
    url = f"https://{HOST}:{port}/preview.html"
    cfg = Config()
    cfg.bind = [f"{HOST}:{port}"]
    cfg.certfile = str(CERT_FILE)
    cfg.keyfile = str(KEY_FILE)
    cfg.alpn_protocols = ["h2", "http/1.1"]
    cfg.accesslog = None
    cfg.errorlog = "-"

    print("=" * 64)
    print("桑珠孜区 · HTTP/2 本地预览")
    print(f"目录：{ROOT}")
    print(f"地址：{url}")
    print("证书：自签名；首次打开需在浏览器选择“继续访问”")
    print("按 Ctrl+C 停止")
    print("=" * 64)

    try:
        asyncio.run(serve(app, cfg))
    except KeyboardInterrupt:
        print("\n已停止。")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
