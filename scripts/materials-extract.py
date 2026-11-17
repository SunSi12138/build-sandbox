#!/usr/bin/env python3
"""把环保材料目录抽取成可读文本，供分析 agent 使用。

输出：
    <out>/index.json          每个文件的路径、类型、字节数、文本长度、状态
    <out>/text/<相对路径>.txt  可提取文本的副本（保留目录结构）

支持 docx/docm、xlsx、pptx、txt/csv；doc/xls/wps/et 走保守的“字符串扫描”，
结果标记为 legacy-crude，分析时需要人工判断；pdf、图片只登记不抽取。
"""
from __future__ import annotations

import argparse
import json
import re
import sys
import zipfile
from pathlib import Path

try:
    import docx
except ImportError:
    docx = None
try:
    import openpyxl
except ImportError:
    openpyxl = None
try:
    from pptx import Presentation
except ImportError:
    Presentation = None

TEXT_SUFFIXES = {".txt", ".csv", ".md", ".json"}
IMAGE_SUFFIXES = {".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp"}
LEGACY_SUFFIXES = {".doc", ".xls", ".wps", ".et", ".docm"}


def read_text_file(path: Path) -> str:
    data = path.read_bytes()
    for encoding in ("utf-8-sig", "utf-8", "gb18030", "utf-16"):
        try:
            return data.decode(encoding)
        except UnicodeDecodeError:
            continue
    return data.decode("utf-8", "replace")


def extract_docx(path: Path) -> str:
    """直接解析 word/document.xml，避免 python-docx 在大文件上加载图片关系而变慢。"""
    import xml.etree.ElementTree as ElementTree

    with zipfile.ZipFile(path) as archive:
        try:
            xml = archive.read("word/document.xml")
        except KeyError as error:
            raise RuntimeError("docx 缺少 word/document.xml") from error
    namespace = "{http://schemas.openxmlformats.org/wordprocessingml/2006/main}"
    root = ElementTree.fromstring(xml)
    lines = []
    for paragraph in root.iter(namespace + "p"):
        pieces = []
        for node in paragraph.iter():
            if node.tag == namespace + "t" and node.text:
                pieces.append(node.text)
            elif node.tag == namespace + "tab":
                pieces.append("\t")
            elif node.tag == namespace + "br":
                pieces.append("\n")
        line = "".join(pieces).strip()
        if line:
            lines.append(line)
    for table in root.iter(namespace + "tbl"):
        rows = []
        for row in table.iter(namespace + "tr"):
            cells = []
            for cell in row.iter(namespace + "tc"):
                text = "".join(node.text or "" for node in cell.iter(namespace + "t")).strip()
                cells.append(text)
            if any(cells):
                rows.append(" | ".join(cells))
        if rows:
            lines.append("[表格]")
            lines.extend(rows)
    return "\n".join(lines)


def extract_xlsx(path: Path) -> str:
    if openpyxl is None:
        raise RuntimeError("缺少 openpyxl")
    workbook = openpyxl.load_workbook(str(path), data_only=True, read_only=True)
    lines = []
    for sheet in workbook.worksheets:
        lines.append(f"## 工作表：{sheet.title}")
        for row in sheet.iter_rows(values_only=True):
            values = ["" if value is None else str(value) for value in row]
            if any(value.strip() for value in values):
                lines.append(" | ".join(values).rstrip(" |"))
    workbook.close()
    return "\n".join(lines)


def extract_pptx(path: Path) -> str:
    if Presentation is None:
        raise RuntimeError("缺少 python-pptx")
    presentation = Presentation(str(path))
    lines = []
    for index, slide in enumerate(presentation.slides, 1):
        lines.append(f"## 第 {index} 页")
        for shape in slide.shapes:
            if hasattr(shape, "text") and shape.text.strip():
                lines.append(shape.text)
    return "\n".join(lines)


def extract_legacy(path: Path) -> str:
    """旧版 doc/xls/wps/et 的保守字符串扫描：抓 UTF-16LE / GB18030 可读片段。"""
    data = path.read_bytes()[:6 * 1024 * 1024]
    chunks = []
    for encoding in ("utf-16-le", "gb18030"):
        try:
            text = data.decode(encoding, "ignore")
        except Exception:
            continue
        for run in re.findall(r"[\u4e00-\u9fff\u3000-\u303fA-Za-z0-9，。；：、（）()《》%\-—…·\s]{8,}", text):
            cleaned = run.strip()
            if len(re.findall(r"[\u4e00-\u9fff]", cleaned)) >= 3:
                chunks.append(cleaned)
    seen, unique = set(), []
    for chunk in chunks:
        if chunk not in seen:
            seen.add(chunk)
            unique.append(chunk)
    return "\n".join(unique[:800])


def extract_pdf(path: Path) -> str:
    """不依赖第三方库的最小 PDF 文本尝试：只处理含 FlateDecode 文本流的简单文件。"""
    import zlib
    data = path.read_bytes()
    pieces = []
    for match in re.finditer(rb"stream\r?\n", data):
        start = match.end()
        end = data.find(b"endstream", start)
        if end < 0:
            continue
        raw = data[start:end]
        try:
            inflated = zlib.decompress(raw)
        except zlib.error:
            continue
        for text in re.findall(rb"\((.*?)\)\s*Tj", inflated):
            pieces.append(text)
        for array in re.findall(rb"\[(.*?)\]\s*TJ", inflated):
            for text in re.findall(rb"\((.*?)\)", array):
                pieces.append(text)
    decoded = []
    for piece in pieces[:4000]:
        try:
            decoded.append(piece.decode("utf-8", "ignore"))
        except Exception:
            continue
    joined = "".join(decoded).strip()
    return joined


def main(argv=None):
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", required=True)
    parser.add_argument("--out", required=True)
    parser.add_argument("--max-bytes", type=int, default=80 * 1024 * 1024)
    args = parser.parse_args(argv)

    source = Path(args.source)
    out = Path(args.out)
    (out / "text").mkdir(parents=True, exist_ok=True)
    index = []
    for path in sorted(source.rglob("*")):
        if not path.is_file():
            continue
        relative = path.relative_to(source)
        suffix = path.suffix.lower()
        record = {"path": str(relative), "suffix": suffix, "bytes": path.stat().st_size,
                  "text": None, "chars": 0, "status": "skipped"}
        if path.stat().st_size > args.max_bytes:
            record["status"] = "too-large"
            index.append(record)
            continue
        try:
            if suffix in IMAGE_SUFFIXES:
                record["status"] = "image"
            elif suffix in TEXT_SUFFIXES:
                text = read_text_file(path)
                record.update(status="text", chars=len(text))
            elif suffix in (".docx", ".docm"):
                text = extract_docx(path)
                record.update(status="docx", chars=len(text))
            elif suffix == ".xlsx":
                text = extract_xlsx(path)
                record.update(status="xlsx", chars=len(text))
            elif suffix == ".pptx":
                text = extract_pptx(path)
                record.update(status="pptx", chars=len(text))
            elif suffix == ".pdf":
                if path.stat().st_size > 15 * 1024 * 1024:
                    record["status"] = "pdf-too-large"
                    text = ""
                else:
                    text = extract_pdf(path)
                    record.update(status="pdf", chars=len(text))
            elif suffix in LEGACY_SUFFIXES:
                text = extract_legacy(path)
                record.update(status="legacy-crude", chars=len(text))
            else:
                record["status"] = "unsupported"
                text = ""
            if record["chars"] and text.strip():
                target = out / "text" / (str(relative) + ".txt")
                target.parent.mkdir(parents=True, exist_ok=True)
                target.write_text(text, encoding="utf-8")
                record["text"] = str(target.relative_to(out))
            elif record["status"] in ("text", "docx", "xlsx", "pptx", "pdf", "legacy-crude"):
                record["status"] += "-empty"
        except Exception as error:  # noqa: BLE001 - 单个文件失败不影响整体
            record["status"] = f"error: {type(error).__name__}: {error}"
        index.append(record)
        if len(index) % 25 == 0:
            print(f"  ... {len(index)} files", flush=True)

    (out / "index.json").write_text(json.dumps(index, ensure_ascii=False, indent=2), encoding="utf-8")
    counts = {}
    for record in index:
        counts[record["status"]] = counts.get(record["status"], 0) + 1
    print(json.dumps({"files": len(index), "status": counts, "out": str(out)}, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    sys.exit(main())
