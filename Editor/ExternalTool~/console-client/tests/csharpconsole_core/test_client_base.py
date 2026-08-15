import unittest

import _bootstrap  # noqa: F401
from csharpconsole_core.client_base import generate_session_id, read_code_from_args, wait_for_service_recovery


class ClientBaseTests(unittest.TestCase):
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
