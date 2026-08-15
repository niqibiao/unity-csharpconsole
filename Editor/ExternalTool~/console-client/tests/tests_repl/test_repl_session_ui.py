import os
import sys
import types
import unittest

SCRIPT_ROOT = os.path.dirname(os.path.abspath(__file__))
CONSOLE_CLIENT_ROOT = os.path.dirname(os.path.dirname(SCRIPT_ROOT))
SITE_PACKAGES_PATH = os.path.join(CONSOLE_CLIENT_ROOT, "site-packages")

if CONSOLE_CLIENT_ROOT not in sys.path:
    sys.path.insert(0, CONSOLE_CLIENT_ROOT)
if SITE_PACKAGES_PATH not in sys.path:
    sys.path.insert(0, SITE_PACKAGES_PATH)

from repl import session_ui


def _make_config(runtime_mode=False, ip="127.0.0.1", port=14500, runtime_ip="127.0.0.1", runtime_port=15500):
    return types.SimpleNamespace(
        runtime_mode=runtime_mode,
        ip=ip,
        port=port,
        runtime_ip=runtime_ip,
        runtime_port=runtime_port,
    )


def _flatten_values(parts):
    return [text for _style, text in parts]


class BuildStartupBannerTests(unittest.TestCase):
    def test_executor_mode_omitted_when_empty(self):
        parts = session_ui.build_startup_banner(_make_config(), "cmd-1", executor_mode="")
        joined = "".join(_flatten_values(parts))
        self.assertNotIn("executor=", joined)

    def test_executor_mode_omitted_when_not_passed(self):
        # backwards-compat: callers from before the executor_mode arg landed
        parts = session_ui.build_startup_banner(_make_config(), "cmd-1")
        joined = "".join(_flatten_values(parts))
        self.assertNotIn("executor=", joined)

    def test_executor_mode_appended_when_hybridclr(self):
        parts = session_ui.build_startup_banner(_make_config(runtime_mode=True), "cmd-1", executor_mode="hybridCLR")
        joined = "".join(_flatten_values(parts))
        self.assertIn("executor=", joined)
        self.assertIn("hybridCLR", joined)

    def test_executor_mode_appended_when_lite(self):
        parts = session_ui.build_startup_banner(_make_config(runtime_mode=True), "cmd-1", executor_mode="lite")
        joined = "".join(_flatten_values(parts))
        self.assertIn("executor=", joined)
        self.assertIn("lite", joined)


class BuildFooterSessionTextTests(unittest.TestCase):
    def test_executor_mode_omitted_when_empty(self):
        parts = session_ui.build_footer_session_text(_make_config(), "cmd-1", executor_mode="")
        joined = "".join(_flatten_values(parts))
        self.assertNotIn("executor=", joined)

    def test_executor_mode_omitted_when_not_passed(self):
        parts = session_ui.build_footer_session_text(_make_config(), "cmd-1")
        joined = "".join(_flatten_values(parts))
        self.assertNotIn("executor=", joined)

    def test_executor_mode_appended_when_hybridclr(self):
        parts = session_ui.build_footer_session_text(_make_config(runtime_mode=True), "cmd-1", executor_mode="hybridCLR")
        joined = "".join(_flatten_values(parts))
        self.assertIn("executor=", joined)
        self.assertIn("hybridCLR", joined)


if __name__ == "__main__":
    unittest.main()
