import argparse
import unittest

import _bootstrap  # noqa: F401
from csharpconsole_core.config_base import SharedConfigState, add_common_connection_args, configure_shared_globals


class ConfigBaseTests(unittest.TestCase):
    def test_add_common_connection_args_adds_shared_flags(self):
        parser = argparse.ArgumentParser()
        add_common_connection_args(parser, lambda p: p.add_argument('--extra', default=''))
        args = parser.parse_args(['--ip', '127.0.0.1', '--port', '14500', '--extra', 'x'])
        self.assertEqual(args.ip, '127.0.0.1')
        self.assertEqual(args.port, 14500)
        self.assertEqual(args.extra, 'x')

    def test_configure_shared_globals_runtime_sets_runtime_target(self):
        state = SharedConfigState()
        args = argparse.Namespace(ip='127.0.0.1', port=15500, editor=False, mode='runtime', runtime_dll_path='dlls', compile_ip='127.0.0.1', compile_port=14500)
        configure_shared_globals(state, args)
        self.assertTrue(state.runtime_mode)
        self.assertEqual(state.runtime_ip, '127.0.0.1')
        self.assertEqual(state.runtime_port, 15500)


if __name__ == '__main__':
    unittest.main()
