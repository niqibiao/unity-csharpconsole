import unittest
import urllib.error
from unittest.mock import patch

import _bootstrap  # noqa: F401
from csharpconsole_core.transport_http import (
    TransportError,
    post_binary,
    post_json,
    post_json_to_execute,
)


class _FakeResponse:
    def __init__(self, body=b"ok", charset="utf-8"):
        self._body = body
        self._charset = charset

    class _Headers:
        def __init__(self, charset):
            self._charset = charset

        def get_content_charset(self):
            return self._charset

    @property
    def headers(self):
        return self._Headers(self._charset)

    def read(self):
        return self._body

    def __enter__(self):
        return self

    def __exit__(self, *exc):
        return False


class TransportHttpTests(unittest.TestCase):
    @patch("urllib.request.urlopen")
    def test_post_json_returns_response_text(self, urlopen_mock):
        urlopen_mock.return_value = _FakeResponse(b"ok")
        result = post_json("http://127.0.0.1:14500/CSharpConsole", "health", {}, 2)
        self.assertEqual(result, "ok")
        urlopen_mock.assert_called_once()

    @patch("urllib.request.urlopen")
    def test_post_json_to_execute_calls_execute_endpoint(self, urlopen_mock):
        urlopen_mock.return_value = _FakeResponse(b"ok")
        post_json_to_execute("http://127.0.0.1:14500/CSharpConsole", {"x": 1}, 2)
        request = urlopen_mock.call_args[0][0]
        self.assertTrue(request.full_url.endswith("/execute"))
        self.assertEqual(request.get_method(), "POST")

    @patch("urllib.request.urlopen")
    def test_post_binary_uses_octet_stream(self, urlopen_mock):
        urlopen_mock.return_value = _FakeResponse(b"ok")
        post_binary("http://x", b"data", 2)
        request = urlopen_mock.call_args[0][0]
        # urllib capitalizes header keys: "Content-Type" -> "Content-type".
        self.assertEqual(request.headers.get("Content-type"), "application/octet-stream")
        self.assertEqual(request.data, b"data")

    @patch("urllib.request.urlopen")
    def test_transport_error_on_connection_failure(self, urlopen_mock):
        urlopen_mock.side_effect = urllib.error.URLError("connection refused")
        with self.assertRaises(TransportError):
            post_json("http://127.0.0.1:14500/CSharpConsole", "health", {}, 2)


if __name__ == "__main__":
    unittest.main()
