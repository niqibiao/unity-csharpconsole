import json
import unittest

import _bootstrap  # noqa: F401
from csharpconsole_core.response_parser import parse_command_http_response, parse_text_http_response


class ResponseParserTests(unittest.TestCase):
    def test_parse_text_http_response_envelope_extracts_text(self):
        raw = json.dumps({
            "ok": True,
            "stage": "execute",
            "type": "ok",
            "summary": "done",
            "sessionId": "sid-1",
            "dataJson": json.dumps({"text": "hello"}),
        })
        result = parse_text_http_response(raw, "execute", "sid-1", "editor", "run-1", 10)
        self.assertTrue(result["ok"])
        self.assertEqual(result["data"]["text"], "hello")

    def test_parse_text_http_response_envelope_preserves_empty_text(self):
        raw = json.dumps({
            "ok": True,
            "stage": "execute",
            "type": "ok",
            "summary": "OK",
            "sessionId": "sid-1",
            "dataJson": json.dumps({"text": ""}),
        })
        result = parse_text_http_response(raw, "execute", "sid-1", "editor", "run-1", 10)
        self.assertTrue(result["ok"])
        self.assertIn("text", result["data"])
        self.assertEqual(result["data"]["text"], "")

    def test_parse_command_http_response_preserves_descriptor(self):
        raw = json.dumps({
            "ok": False,
            "stage": "command",
            "type": "unsupported",
            "summary": "Command cannot satisfy this request",
            "sessionId": "sid-1",
            "dataJson": json.dumps({
                "command": {
                    "commandNamespace": "project",
                    "action": "scene.open",
                    "summary": "Open a scene",
                },
                "resultJson": {"reason": "missing-scene"},
            }),
        })

        result = parse_command_http_response(raw, "sid-1", "editor", "run-1", 10)

        self.assertFalse(result["ok"])
        self.assertEqual(result["type"], "unsupported")
        self.assertEqual(result["data"]["command"]["summary"], "Open a scene")
        self.assertEqual(result["data"]["resultJson"], {"reason": "missing-scene"})

    def test_parse_text_http_response_legacy_forward_failure_is_runtime_error(self):
        result = parse_text_http_response("Forward failed: timeout", "execute", "sid-1", "runtime", "run-1", 10)
        self.assertFalse(result["ok"])
        self.assertEqual(result["type"], "runtime_error")
        self.assertEqual(result["exitCode"], 2)


if __name__ == "__main__":
    unittest.main()
