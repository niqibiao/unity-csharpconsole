import unittest

import _bootstrap  # noqa: F401
import json
import urllib.parse

from csharpconsole_core.client_base import generate_session_id, read_code_from_args, request_compile_set, wait_for_service_recovery
from csharpconsole_core.response_parser import parse_compile_set_http_response
from csharpconsole_core.transport_http import TransportError


def _ok_envelope(summary):
    return json.dumps({"ok": True, "stage": "bootstrap", "type": "ok", "summary": summary, "sessionId": "", "dataJson": "{}"})


class ClientBaseTests(unittest.TestCase):
    def test_request_compile_set_sends_the_zip_for_the_target_player(self):
        calls = []

        def post_binary(url, body, timeout):
            calls.append((url, body))
            return _ok_envelope("Registered")

        result = request_compile_set(post_binary, parse_compile_set_http_response, "http://127.0.0.1:14500/CSharpConsole", "10.0.0.5", 15500, b"zip")
        self.assertTrue(result["ok"])
        url, body = calls[0]
        parsed = urllib.parse.urlsplit(url)
        self.assertEqual(parsed.path, "/CSharpConsole/compile-set")
        self.assertEqual(urllib.parse.parse_qs(parsed.query), {"targetIP": ["10.0.0.5"], "targetPort": ["15500"]})
        self.assertEqual(body, b"zip")

    def test_request_compile_set_skip_sends_no_zip(self):
        calls = []

        def post_binary(url, body, timeout):
            calls.append((url, body))
            return _ok_envelope("Skipped")

        request_compile_set(post_binary, parse_compile_set_http_response, "http://127.0.0.1:14500/CSharpConsole", "10.0.0.5", 15500, skip=True)
        url, body = calls[0]
        self.assertEqual(urllib.parse.parse_qs(urllib.parse.urlsplit(url).query)["skip"], ["true"])
        self.assertEqual(body, b"")

    def test_request_compile_set_reports_transport_failure(self):
        def post_binary(url, body, timeout):
            raise TransportError("connection refused")

        result = request_compile_set(post_binary, parse_compile_set_http_response, "http://127.0.0.1:14500/CSharpConsole", "10.0.0.5", 15500, b"zip")
        self.assertFalse(result["ok"])
        self.assertEqual(result["type"], "system_error")
        self.assertIn("connection refused", result["summary"])

    def test_generate_session_id_uses_explicit_value(self):
        self.assertEqual(generate_session_id('sid-1'), 'sid-1')

    def test_read_code_from_args_uses_inline_code(self):
        class Args:
            code = 'Debug.Log(1);'
            code_file = None
        self.assertEqual(read_code_from_args(Args()), 'Debug.Log(1);')

    def test_wait_for_service_recovery_returns_when_health_ready(self):
        def request_health():
            return {
                'ok': True,
                'data': {
                    'initialized': True,
                    'editorState': 'ready',
                    'operation': {'phase': 'ready'},
                },
                'summary': 'ok',
            }

        def current_mode_name():
            return 'editor'

        result = wait_for_service_recovery(request_health, current_mode_name, 1, poll_interval_seconds=0.01)
        self.assertTrue(result['ok'])
        self.assertEqual(result['summary'], 'Unity service recovered after refresh')

    def test_wait_for_service_recovery_returns_failed_phase(self):
        def request_health():
            return {
                'ok': True,
                'data': {
                    'operation': {'phase': 'failed', 'message': 'bad'},
                    'editorState': 'compiling',
                },
                'summary': 'bad',
            }

        def current_mode_name():
            return 'editor'

        result = wait_for_service_recovery(request_health, current_mode_name, 1, poll_interval_seconds=0.01)
        self.assertFalse(result['ok'])
        self.assertEqual(result['summary'], 'bad')

    def test_wait_for_service_recovery_times_out(self):
        def request_health():
            return {'ok': False, 'summary': 'still waiting'}

        def current_mode_name():
            return 'editor'

        result = wait_for_service_recovery(request_health, current_mode_name, 0.02, poll_interval_seconds=0.01)
        self.assertFalse(result['ok'])
        self.assertIn('Timed out waiting for Unity service recovery', result['summary'])


if __name__ == '__main__':
    unittest.main()
