import concurrent.futures
import os
import pathlib
import socket
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid

exe = str(pathlib.Path(sys.argv[1]).resolve())
with socket.socket() as s:
    s.bind(("127.0.0.1", 0))
    port = s.getsockname()[1]
base = f"http://127.0.0.1:{port}"
chunk = 2 * 1024 * 1024
def req(path, data=None, header=True):
    headers = {"X-File-Assistant":"1"} if header else {}
    return urllib.request.urlopen(urllib.request.Request(base+path, data=data, headers=headers), timeout=45).read().decode("utf-8")
def endpoint(path, ident, **params):
    return "/api/" + path + "?" + urllib.parse.urlencode(dict(id=ident, **params))
def put(name, data, replay=False):
    ident = uuid.uuid4().hex
    init = endpoint("init", ident, name=name, size=len(data))
    assert req(init,b"") == "0"
    off = 0
    while off < len(data):
        payload = data[off:off+chunk]
        path = endpoint("chunk",ident,offset=off)
        nextoff = off + len(payload)
        assert req(path,payload) == str(nextoff)
        if replay:
            assert req(path,payload) == str(nextoff)
            assert req(init,b"") == str(nextoff)
        off = nextoff
    finish = endpoint("finish",ident)
    result = req(finish,b"")
    assert result in ("saved","duplicate")
    assert req(finish,b"") == result
    assert req(init,b"") == result
    return result
def start(root):
    p = subprocess.Popen([exe,"--ip","127.0.0.1","--port",str(port),"--dir",str(root)])
    try:
        for _ in range(200):
            if p.poll() is not None: raise RuntimeError("executable exited")
            try:
                if req("/health")=="ok": return p
            except OSError: time.sleep(.1)
        raise RuntimeError("startup timeout")
    except BaseException:
        p.kill()
        p.wait()
        raise
def stop(p):
    p.terminate()
    try: p.wait(timeout=10)
    except subprocess.TimeoutExpired:
        p.kill()
        p.wait()
with tempfile.TemporaryDirectory() as d:
    root = pathlib.Path(d)/"files"
    p = start(root)
    large = os.urandom(chunk+23)
    try:
        assert 'type="file"' in req("/")
        try:
            req(endpoint("init",uuid.uuid4().hex,name="denied",size=0),b"",False)
            raise AssertionError("missing origin header accepted")
        except urllib.error.HTTPError as e: assert e.code==403
        assert put("资料.pdf", large, True) == "saved"
        assert put("另一个名字.pdf", large) == "duplicate"
        assert put("资料.pdf", b"different") == "saved"
        assert put("../../escape.txt", b"no traversal") == "saved"
        assert not (root.parent/"escape.txt").exists()
        assert put("empty", b"") == "saved"
        with concurrent.futures.ThreadPoolExecutor(max_workers=4) as pool:
            results = list(pool.map(lambda i: put(f"parallel{i}.txt", b"concurrent"), range(4)))
        assert results.count("saved")==1 and results.count("duplicate")==3, results
        assert (root/"资料.pdf").read_bytes()==large
        assert (root/"资料 (1).pdf").read_bytes()==b"different"
        assert (root/"escape.txt").read_bytes()==b"no traversal"
        assert len([x for x in root.iterdir() if x.is_file()])==5
    finally: stop(p)
    p = start(root)
    try:
        assert put("restart.pdf", large)=="duplicate"
        assert len([x for x in root.iterdir() if x.is_file()])==5
    finally: stop(p)
print("PASS: NativeAOT HTTP, retry, duplicate content, concurrent upload, same-name collision, path sanitization and restart")
