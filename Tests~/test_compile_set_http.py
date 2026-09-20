"""Live registration regressions; set CSHARPCONSOLE_TEST_URL to an Editor service.

All requests must be rejected before registering a set. Fake players run on
localhost, so the Editor must run on this machine too.
"""

import contextlib
import io
import json
import os
import socket
import threading
import unittest
import urllib.parse
import urllib.request
import uuid
import zipfile
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer


BASE_URL = os.environ.get("CSHARPCONSOLE_TEST_URL", "").rstrip("/")


@contextlib.contextmanager
def player(response):
    class Handler(BaseHTTPRequestHandler):
        def do_POST(self):
            self.rfile.read(int(self.headers.get("Content-Length", 0)))
            payload = json.dumps(response).encode("utf-8")
            self.send_response(200)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(payload)))
            self.end_headers()
            self.wfile.write(payload)

        def log_message(self, *args):
            pass

    server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    try:
        yield server.server_port
    finally:
        server.shutdown()
        server.server_close()
        thread.join()


@unittest.skipUnless(BASE_URL, "set CSHARPCONSOLE_TEST_URL for live Editor tests")
class CompileSetHttpTests(unittest.TestCase):
    def register(self, port, *, skip=False):
        query = {"targetIP": "127.0.0.1", "targetPort": str(port)}
        body = b""
        if skip:
            query["skip"] = "true"
        else:
            stream = io.BytesIO()
            with zipfile.ZipFile(stream, "w") as archive:
                archive.writestr("build-guid.txt", uuid.uuid4().hex)
            body = stream.getvalue()
        request = urllib.request.Request(
            BASE_URL + "/compile-set?" + urllib.parse.urlencode(query),
            data=body, headers={"Content-Type": "application/octet-stream"},
        )
        with urllib.request.urlopen(request, timeout=30) as response:
            return json.load(response)

    def assert_rejected(self, result, text):
        self.assertFalse(result["ok"], result)
        self.assertEqual("validation_error", result["type"])
        self.assertIn(text, result["summary"])

    def test_unreachable_player_cannot_register_or_skip(self):
        # Reserve an unused port without listening; no other process can claim it.
        with socket.socket() as reserved:
            reserved.bind(("127.0.0.1", 0))
            for skip in (False, True):
                with self.subTest(skip=skip):
                    self.assert_rejected(
                        self.register(reserved.getsockname()[1], skip=skip),
                        "could not be reached",
                    )

    def test_invalid_health_cannot_register_or_skip(self):
        for response in (
            {"ok": True},
            {"ok": False, "dataJson": json.dumps({"ok": True})},
            {"ok": True, "dataJson": json.dumps({"ok": True, "isEditor": True})},
        ):
            with player(response) as port:
                for skip in (False, True):
                    with self.subTest(response=response, skip=skip):
                        self.assert_rejected(self.register(port, skip=skip), "health answered without valid player data")

    def test_different_build_is_still_rejected(self):
        with player({"ok": True, "dataJson": json.dumps({"ok": True, "buildGuid": uuid.uuid4().hex})}) as port:
            self.assert_rejected(self.register(port), "but the player is build")

    def test_players_without_a_valid_build_identity_must_be_rebuilt(self):
        for guid in (None, "", "not-a-build-guid"):
            health = {"ok": True}
            if guid is not None:
                health["buildGuid"] = guid
            with player({"ok": True, "dataJson": json.dumps(health)}) as port:
                for skip in (False, True):
                    with self.subTest(guid=guid, skip=skip):
                        self.assert_rejected(self.register(port, skip=skip), "rebuild the Player")


if __name__ == "__main__":
    unittest.main()
