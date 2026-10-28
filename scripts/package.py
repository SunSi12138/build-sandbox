#!/usr/bin/env python3
"""Package the Linux application, optionally with the Windows executable."""
import argparse
import hashlib
from pathlib import Path
import struct
import zipfile


def verify_binary(path: Path, platform: str) -> None:
    if not path.is_file() or path.is_symlink():
        raise ValueError(f"Missing regular executable: {path}")
    with path.open("rb") as source:
        header = source.read(64)
        if platform == "win-x64":
            if header[:2] != b"MZ":
                raise ValueError("Windows executable is not a PE file")
            source.seek(struct.unpack_from("<I", header, 60)[0])
            pe = source.read(6)
            if pe[:4] != b"PE\0\0" or struct.unpack_from("<H", pe, 4)[0] != 0x8664:
                raise ValueError("Windows executable is not x64")
        elif header[:5] != b"\x7fELF\x02" or struct.unpack_from("<H", header, 18)[0] != 183:
            raise ValueError("Linux executable is not ARM64 ELF")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--win-x64", type=Path, help="Include Windows x64 in the combined package")
    parser.add_argument("--linux-arm64", type=Path, required=True)
    parser.add_argument("--web-static", type=Path, required=True)
    parser.add_argument("--output", type=Path, default=Path("dist"))
    args = parser.parse_args()
    if args.win_x64 is not None:
        verify_binary(args.win_x64, "win-x64")
    verify_binary(args.linux_arm64, "linux-arm64")
    if not (args.web_static / "index.html").is_file():
        raise ValueError("Missing generated frontend index.html")
    files = [(args.linux_arm64, "hbtool/StreetEcoTool-linux-arm64", 0o755)]
    if args.win_x64 is not None:
        files.insert(0, (args.win_x64, "hbtool/StreetEcoTool-win-x64.exe", 0o644))
    for file in sorted(args.web_static.rglob("*")):
        if file.is_symlink():
            raise ValueError(f"Frontend cannot include symbolic links: {file}")
        if file.is_file():
            files.append((file, "hbtool/wwwroot/" + file.relative_to(args.web_static).as_posix(), 0o644))
    root = Path(__file__).resolve().parent.parent
    files.extend([(root / "scripts/linux/start-linux.sh", "hbtool/start-linux.sh", 0o755),
                  (root / "scripts/linux/启动应用.desktop", "hbtool/启动应用.desktop", 0o755)])
    args.output.mkdir(parents=True, exist_ok=True)
    target = args.output / ("hbtool.zip" if args.win_x64 is not None else "hbtool-linux-arm64.zip")
    with zipfile.ZipFile(target, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
        for source, name, mode in files:
            entry = zipfile.ZipInfo(name)
            entry.create_system = 3
            entry.external_attr = (0o100000 | mode) << 16
            entry.compress_type = zipfile.ZIP_DEFLATED
            with source.open("rb") as input_file, archive.open(entry, "w") as output:
                while block := input_file.read(1024 * 1024):
                    output.write(block)
    with zipfile.ZipFile(target) as archive:
        if archive.testzip() is not None:
            raise ValueError("Package CRC verification failed")
        names = archive.namelist()
        if any("linux-x64" in name or name.endswith("appsettings.json") for name in names):
            raise ValueError("Package includes a test binary or user configuration")
        assert (archive.getinfo("hbtool/StreetEcoTool-linux-arm64").external_attr >> 16) & 0o777 == 0o755
    if args.win_x64 is None:
        print(f"Packaged {len(files)} files into {target} ({target.stat().st_size:,} bytes)")
        return
    checksum_hash = hashlib.sha256()
    with target.open("rb") as package_file:
        while block := package_file.read(1024 * 1024):
            checksum_hash.update(block)
    digest = checksum_hash.hexdigest()
    checksum = args.output / "hbtool.zip.sha256"
    checksum.write_text(f"{digest}  hbtool.zip\n", encoding="ascii")
    print(f"Packaged {len(files)} files into {target} ({target.stat().st_size:,} bytes)")
    print(f"SHA-256: {digest}")


if __name__ == "__main__":
    main()
