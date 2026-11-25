"""Integration checks for download priority using a simulated camera HTTP proxy.

Run after building the project: python3 tests/test_priority.py
"""

import json
import os
from pathlib import Path
import platform
import socket
import subprocess
import tempfile
import threading
import time
import urllib.error
import urllib.parse
import urllib.request
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer


ROOT = Path(__file__).resolve().parent.parent
names = ["a.mp4", "b.mp4", "c.mp4"]
started = threading.Event()
release = threading.Event()
downloads = []
lock = threading.Lock()


class Camera(BaseHTTPRequestHandler):
    def log_message(self, *args):
        pass

    def do_HEAD(self):
        self.send_response(200)
        self.send_header("Content-Length", "4")
        self.end_headers()

    def do_GET(self):
        url = urllib.parse.urlsplit(self.path)
        path = url.path + ("?" + url.query if url.query else "")
        if path.startswith("/?custom=1&cmd=3015"):
            with lock:
                listing = list(names)
            body = ("<LIST>" + "".join(
                f"<File><NAME>{name}</NAME><FPATH>A:\\DCIM\\{name}</FPATH>"
                f"<TIME>2024/01/01 00:00:0{index}</TIME><SIZE>4</SIZE></File>"
                for index, name in enumerate(listing)
            ) + "</LIST>").encode()
        elif path.startswith("/DCIM/") and path.endswith(".mp4"):
            name = path.rsplit("/", 1)[-1]
            with lock:
                downloads.append(name)
            if name == "a.mp4":
                started.set()
                if not release.wait(20):
                    raise RuntimeError("Timed out waiting to release the first transfer")
            body = b"data"
        else:
            body = b"<html><table></table></html>"
        self.send_response(200)
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)


def request(port, path, payload=None):
    body = None if payload is None else json.dumps(payload).encode()
    req = urllib.request.Request(
        f"http://127.0.0.1:{port}{path}",
        data=body,
        headers={"Content-Type": "application/json"},
    )
    try:
        with urllib.request.urlopen(req, timeout=10) as response:
            return response.status, json.load(response)
    except urllib.error.HTTPError as error:
        return error.code, json.load(error)


def main():
    camera = ThreadingHTTPServer(("127.0.0.1", 0), Camera)
    threading.Thread(target=camera.serve_forever, daemon=True).start()
    with socket.socket() as sock:
        sock.bind(("127.0.0.1", 0))
        dashboard_port = sock.getsockname()[1]

    with tempfile.TemporaryDirectory() as downloads_root:
        rid = {
            ("Darwin", "arm64"): "osx-arm64",
            ("Linux", "x86_64"): "linux-x64",
            ("Linux", "aarch64"): "linux-arm64",
        }.get((platform.system(), platform.machine()))
        if rid is None:
            raise RuntimeError("The priority integration test does not support this platform.")
        env = {key: value for key, value in os.environ.items()
               if not key.startswith("DASHCAM_CAMERA_") and key != "CAMERAS_JSON"}
        env.update({
            "DASHCAM_DOWNLOAD_ROOT": downloads_root,
            "DASHCAM_DASHBOARD_URL": f"http://127.0.0.1:{dashboard_port}",
            "DASHCAM_REQUIRE_CAMERA_SETTINGS": "true",
            "DASHCAM_CAMERA_1_IP": "192.0.2.27",
            "DASHCAM_CAMERA_1_NAME": "test",
            "HTTP_PROXY": f"http://127.0.0.1:{camera.server_port}",
            "http_proxy": f"http://127.0.0.1:{camera.server_port}",
            "NO_PROXY": "127.0.0.1,localhost",
            "no_proxy": "127.0.0.1,localhost",
        })
        with tempfile.TemporaryFile(mode="w+") as output:
            process = subprocess.Popen(
                ["dotnet", str(ROOT / "bin" / "Debug" / "net8.0" / rid / "DashCamSync.dll")],
                cwd=ROOT, env=env, stdout=output, stderr=subprocess.STDOUT,
            )
            try:
                deadline = time.monotonic() + 15
                while time.monotonic() < deadline:
                    try:
                        status, _ = request(dashboard_port, "/api/status")
                        if status == 200:
                            break
                    except (OSError, urllib.error.URLError):
                        time.sleep(0.1)
                else:
                    raise AssertionError("Dashboard did not start")

                assert started.wait(10), "First download did not start"
                endpoint = "/api/cameras/192.0.2.27/files/prioritize"
                status, result = request(dashboard_port, endpoint, {"paths": ["c.mp4"]})
                assert status == 202 and result["queuedPaths"] == ["c.mp4"], (status, result)
                status, result = request(dashboard_port, endpoint, {"paths": ["c.mp4", "b.mp4"]})
                assert status == 202 and result["queuedPaths"] == ["c.mp4", "b.mp4"], (status, result)
                status, _ = request(dashboard_port, endpoint, {"paths": ["b.mp4", "b.mp4"]})
                assert status == 400
                status, _ = request(dashboard_port, endpoint, {"paths": ["../b.mp4"]})
                assert status == 400
                status, _ = request(dashboard_port, endpoint, {"paths": ["missing.mp4"]})
                assert status == 409
                with lock:
                    names.append("d.mp4")
                status, result = request(dashboard_port, endpoint, {"paths": ["d.mp4"]})
                assert status == 202, (status, result)
                release.set()
                deadline = time.monotonic() + 15
                while time.monotonic() < deadline:
                    with lock:
                        if len(downloads) >= 4:
                            break
                    time.sleep(0.1)
                with lock:
                    assert downloads == ["a.mp4", "d.mp4", "c.mp4", "b.mp4"], downloads
                assert (Path(downloads_root) / "test" / "20240101" / "a.mp4").exists()
                status, _ = request(dashboard_port, endpoint, {"paths": ["a.mp4"]})
                assert status == 409, "A downloaded file was accepted for priority"
                print("Priority integration checks passed.")
            except Exception:
                output.seek(0)
                print(output.read())
                raise
            finally:
                release.set()
                process.terminate()
                process.wait(timeout=10)
                camera.shutdown()
                camera.server_close()


if __name__ == "__main__":
    main()
