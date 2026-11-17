#!/usr/bin/env python3
# -*- coding: utf-8 -*-
r"""
吉林一号 2025 全国高质量一张图（0.5m）瓦片下载 —— 传 z x y 即下

用法:
    python jl1_tile.py 18 195792 108765
    python jl1_tile.py 195792 108765 18              # x y z 顺序也认
    python jl1_tile.py 18 195792 108765 -o D:\tiles  # 指定输出目录
    python jl1_tile.py --list tiles.txt -o D:\tiles  # 批量（每行 "x y" 或 "z x y"）
    python jl1_tile.py --list tiles.txt --z 18 -o D:\tiles --workers 6

通道: jlcdn.thread0.com + 本地 wasm 签名（signer.js），
      不消耗 jl1mall 账号额度，也不需要浏览器/登录。

输出: <out>/{z}/{x}/{y}.webp（XYZ 编号）
"""
import argparse
import json
import os
import shutil
import subprocess
import sys
import threading
import time
import urllib.error
import urllib.request

HERE = os.path.dirname(os.path.abspath(__file__))
CDN = "https://jlcdn.thread0.com"
TILE_PATH = "/maptile/cn/2025/v2/{x}/{y}/{z}"      # XYZ
UA = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/120.0 Safari/537.36"
HEADERS = {"User-Agent": UA, "Referer": "https://pc.earthdq.com/",
           "Accept": "image/webp,image/*,*/*;q=0.8"}


def find_node() -> str:
    for cand in (os.environ.get("NODE"), shutil.which("node"), shutil.which("node.exe")):
        if cand and os.path.exists(cand):
            return cand
    # DSH 运行时里自带的 node
    guess = os.path.expanduser(
        r"~\.dsh\dsh-runtimes\dsh-primary-runtime\dependencies\node\bin\node.exe")
    if os.path.exists(guess):
        return guess
    raise SystemExit("找不到 node.exe，请把 node 加进 PATH 或用环境变量 NODE 指定")


class Signer:
    """常驻 node 进程，负责把 path 批量签成 auth_key"""

    def __init__(self, verbose=True):
        self.proc = subprocess.Popen(
            [find_node(), os.path.join(HERE, "signer.js")],
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
            text=True, encoding="utf-8", bufsize=1)
        line = self.proc.stdout.readline()
        try:
            hello = json.loads(line)
        except Exception:
            hello = {"error": (line or "").strip() or "signer 无输出"}
        if "error" in hello:
            raise SystemExit("签名器启动失败: " + hello["error"])
        if verbose:
            print("  签名器已就绪（wasm）")

    def sign(self, paths):
        self.proc.stdin.write(json.dumps({"paths": list(paths)}) + "\n")
        self.proc.stdin.flush()
        line = self.proc.stdout.readline()
        if not line:
            raise SystemExit("签名器意外退出: " + (self.proc.stderr.read() or "")[:300])
        res = json.loads(line)
        if "error" in res:
            raise SystemExit("签名失败: " + res["error"])
        sigs = res["sigs"]
        if len(sigs) != len(paths):
            raise SystemExit("签名数量不一致")
        return sigs

    def close(self):
        try:
            self.proc.stdin.close()
            self.proc.terminate()
        except Exception:
            pass


def fetch(url, timeout=25, retries=2):
    for i in range(retries + 1):
        try:
            with urllib.request.urlopen(urllib.request.Request(url, headers=HEADERS), timeout=timeout) as r:
                return r.status, r.read()
        except urllib.error.HTTPError as e:
            if e.code in (403, 429) and i < retries:
                time.sleep(0.8 + i)
                continue
            return e.code, e.read()[:200]
        except Exception:
            if i < retries:
                time.sleep(0.6 + i)
    return None, b""


def save(out, z, x, y, body):
    d = os.path.join(out, str(z), str(x))
    os.makedirs(d, exist_ok=True)
    p = os.path.join(d, f"{y}.webp")
    tmp = p + ".part"
    with open(tmp, "wb") as f:
        f.write(body)
    os.replace(tmp, p)
    return p


def normalize(nums):
    """三个整数 -> (z, x, y)；兼容 x y z 顺序"""
    if nums[0] <= 22 and (nums[1] > 22 or nums[2] > 22):
        z, x, y = nums
    elif nums[2] <= 22:
        x, y, z = nums
    else:
        z, x, y = nums
    n = 1 << z
    if not (0 <= z <= 22) or not (0 <= x < n) or not (0 <= y < n):
        raise SystemExit(f"瓦片号不合法: z={z} x={x} y={y}（z{z} 范围 0..{n-1}）")
    return z, x, y


def main():
    ap = argparse.ArgumentParser(description="吉林一号 2025 全国一张图 z18 瓦片下载（传 z x y）")
    ap.add_argument("xyz", nargs="*", type=int, help="z x y（或 x y z）")
    ap.add_argument("--list", help="批量文件，每行 'x y' 或 'z x y'")
    ap.add_argument("--z", type=int, default=None, help="批量模式下统一缩放级别")
    ap.add_argument("-o", "--out", default=".", help="输出目录，默认当前目录")
    ap.add_argument("--workers", type=int, default=4, help="并发线程，默认 4")
    ap.add_argument("--skip-existing", action="store_true", help="已存在则跳过")
    args = ap.parse_args()

    jobs = []
    if args.list:
        z_default = args.z
        with open(args.list, encoding="utf-8") as f:
            for line in f:
                parts = line.strip().replace(",", " ").split()
                if not parts or parts[0].startswith("#"):
                    continue
                nums = [int(p) for p in parts[:3]]
                if len(nums) == 2:
                    if z_default is None:
                        raise SystemExit("批量文件只有 x y，需要 --z")
                    z, x, y = z_default, nums[0], nums[1]
                else:
                    z, x, y = normalize(nums)
                jobs.append((z, x, y))
    else:
        if len(args.xyz) != 3:
            ap.print_help()
            return 2
        jobs.append(normalize(args.xyz))

    if args.skip_existing:
        jobs = [j for j in jobs
                if not os.path.exists(os.path.join(args.out, str(j[0]), str(j[1]), f"{j[2]}.webp"))]
        if not jobs:
            print("全部已存在，无需下载")
            return 0

    signer = Signer()
    t0 = time.time()
    ok = fail = empty = 0
    total_bytes = 0
    lock = threading.Lock()

    BATCH = 200
    for start in range(0, len(jobs), BATCH):
        chunk = jobs[start:start + BATCH]
        paths = [TILE_PATH.format(x=x, y=y, z=z) for z, x, y in chunk]
        sigs = signer.sign(paths)

        def work(item):
            (z, x, y), p, s = item
            st, body = fetch(CDN + p + "?auth_key=" + s)
            if st == 200 and body[:4] == b"RIFF" and len(body) > 300:
                path = save(args.out, z, x, y, body)
                return ("ok", len(body), path)
            if st == 200 and body[:4] == b"RIFF":
                return ("empty", len(body), None)
            return ("fail", len(body), f"HTTP {st}")

        from concurrent.futures import ThreadPoolExecutor
        with ThreadPoolExecutor(max_workers=max(1, args.workers)) as ex:
            for status, n, extra in ex.map(work, zip(chunk, paths, sigs)):
                with lock:
                    if status == "ok":
                        ok += 1
                        total_bytes += n
                        if len(jobs) == 1:
                            print(f"OK  {chunk[0][0]} {chunk[0][1]} {chunk[0][2]}  {n} 字节  ->  {extra}")
                    elif status == "empty":
                        empty += 1
                    else:
                        fail += 1
                        print(f"失败 {extra}")

    signer.close()
    dt = time.time() - t0
    if len(jobs) > 1:
        print(f"完成: 成功 {ok} 空图 {empty} 失败 {fail}  {total_bytes/1048576:.2f} MB  "
              f"{dt:.1f}s")
    return 0 if fail == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
