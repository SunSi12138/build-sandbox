吉林一号 2025 全国高质量一张图（0.5m）瓦片下载工具
====================================================

传 z x y 三个数，下对应的那一张瓦片。不需要登录、不需要账号额度、不需要浏览器。

目录内容
--------
  jl1_tile.py         命令行工具（Python 3，只用标准库）
  signer.js           签名进程（Node）
  security.wasm       签名逻辑（来自 pc.earthdq.com，本地跑）
  wasm_exec_tiny.js   Go/WASM 运行垫片
  tiles.txt           批量示例（每行 "x y"）

用法
----
  单张：
    python jl1_tile.py 18 195792 108765
    python jl1_tile.py 18 195792 108765 -o D:\tiles
    python jl1_tile.py 195792 108765 18            （x y z 顺序也认）

  批量（每行 "x y"，用 --z 指定级别；也可每行写 "z x y"）：
    python jl1_tile.py --list tiles.txt --z 18 -o D:\tiles --workers 16

  已下载的跳过：
    python jl1_tile.py --list tiles.txt --z 18 -o D:\tiles --workers 16 --skip-existing

输出
----
  <out>/18/<x>/<y>.webp      XYZ 编号、WebP、256×256

速度
----
  实测 --workers 16 约 24 张/秒（受对方 CDN 限速影响，波动较大）。

注意
----
  1. 数据源是 jlcdn.thread0.com（Earth元地球的 CDN），瓦片内容与吉林一号官网接口
     逐字节一致；这是第三方通道，对方随时可能改签名或加限速。
  2. 需要本机有 node（脚本会自动找 PATH 里的 node，或 DSH 运行时自带的 node）。
  3. 空图（<300 字节）不会落盘。
