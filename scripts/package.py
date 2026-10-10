#!/usr/bin/env python3
"""Package only the NativeAOT executable and generated offline wwwroot."""
import argparse
import hashlib
import os
from pathlib import Path
import tarfile
import tempfile
import zipfile
import shutil


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--rid', required=True, choices=['win-x86', 'linux-arm64', 'linux-x64'])
    parser.add_argument('--publish', type=Path, default=Path('publish'))
    parser.add_argument('--output', type=Path, default=Path('dist'))
    args = parser.parse_args()
    binary_name = 'StreetEcoTool.exe' if args.rid.startswith('win-') else 'StreetEcoTool'
    binary = args.publish / binary_name
    public = args.publish / 'wwwroot'
    if not binary.is_file() or not (public / 'index.html').is_file():
        raise SystemExit('Packaging requires the native executable and wwwroot/index.html.')
    if any(p.name in ('e_sqlite3.dll', 'libe_sqlite3.so', 'libe_sqlite3.dylib') for p in args.publish.rglob('*')):
        raise SystemExit('SQLite must be statically linked; a dynamic SQLite library was found.')
    args.output.mkdir(parents=True, exist_ok=True)
    suffix = '.zip' if args.rid.startswith('win-') else '.tar.gz'
    destination = args.output / ('hbtool-' + args.rid + suffix)
    with tempfile.TemporaryDirectory(prefix='hbtool-package-') as temporary:
        root = Path(temporary) / 'hbtool'
        root.mkdir()
        shutil.copy2(binary, root / binary_name)
        shutil.copytree(public, root / 'wwwroot')
        if not args.rid.startswith('win-'):
            (root / binary_name).chmod(0o755)
        if suffix == '.zip':
            with zipfile.ZipFile(destination, 'w', compression=zipfile.ZIP_DEFLATED, compresslevel=6) as archive:
                for path in sorted(root.rglob('*')):
                    if path.is_file():
                        archive.write(path, path.relative_to(root.parent).as_posix())
        else:
            with tarfile.open(destination, 'w:gz', compresslevel=6) as archive:
                archive.add(root, arcname='hbtool')
    digest = hashlib.sha256(destination.read_bytes()).hexdigest()
    destination.with_suffix(destination.suffix + '.sha256').write_text(digest + '  ' + destination.name + '\n', encoding='ascii')
    print(f'{destination}: {destination.stat().st_size:,} bytes; SHA256 {digest}')


if __name__ == '__main__':
    main()
