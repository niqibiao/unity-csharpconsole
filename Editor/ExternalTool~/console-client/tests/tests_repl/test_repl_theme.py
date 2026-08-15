import io
import os
import sys
import tempfile
import unittest
from contextlib import redirect_stdout

SCRIPT_ROOT = os.path.dirname(os.path.abspath(__file__))
CONSOLE_CLIENT_ROOT = os.path.dirname(os.path.dirname(SCRIPT_ROOT))
SITE_PACKAGES_PATH = os.path.join(CONSOLE_CLIENT_ROOT, "site-packages")
_ADDED_SITE_PACKAGES_PATH = False

if CONSOLE_CLIENT_ROOT not in sys.path:
    sys.path.insert(0, CONSOLE_CLIENT_ROOT)

if SITE_PACKAGES_PATH not in sys.path:
    sys.path.insert(0, SITE_PACKAGES_PATH)
    _ADDED_SITE_PACKAGES_PATH = True

CORE_PATH = os.path.join(CONSOLE_CLIENT_ROOT, "csharpconsole_core")
_ADDED_CORE_PATH = False
if CORE_PATH not in sys.path:
    sys.path.insert(0, CORE_PATH)
    _ADDED_CORE_PATH = True

try:
    from prompt_toolkit.completion import CompleteEvent
    from prompt_toolkit.document import Document
    from repl.completion import BuiltinCmdCompleter, ThemeCompleter, trigger_completion_on_change
    from repl.theme import DEFAULT_THEME, ThemeManager, list_themes
    import csharp_repl_core as repl
finally:
    if _ADDED_CORE_PATH:
        sys.path.remove(CORE_PATH)

    if _ADDED_SITE_PACKAGES_PATH:
        sys.path.remove(SITE_PACKAGES_PATH)


def _pick_non_default_theme():
    for name in list_themes():
        if name != DEFAULT_THEME:
            return name
    raise AssertionError("expected at least two pygments themes")


class FakeThemeBuffer:
    def __init__(self, text):
        self.document = Document(text)
        self.started_completion = False

    def start_completion(self, select_first=False):
        self.started_completion = True


class ThemeManagerTests(unittest.TestCase):
    def test_defaults_to_material(self):
        with tempfile.TemporaryDirectory() as tmp:
            manager = ThemeManager(None, cache_dir=tmp)
            self.assertEqual(DEFAULT_THEME, "material")
            self.assertEqual(manager.current_theme(), DEFAULT_THEME)
            self.assertEqual(manager.active_theme(), DEFAULT_THEME)

    def test_set_theme_valid_and_invalid(self):
        with tempfile.TemporaryDirectory() as tmp:
            manager = ThemeManager(None, cache_dir=tmp)
            other = _pick_non_default_theme()
            self.assertTrue(manager.set_theme(other))
            self.assertEqual(manager.current_theme(), other)
            self.assertFalse(manager.set_theme("not-a-real-theme"))
            self.assertEqual(manager.current_theme(), other)

    def test_set_theme_persists_across_instances(self):
        with tempfile.TemporaryDirectory() as tmp:
            other = _pick_non_default_theme()
            ThemeManager(None, cache_dir=tmp).set_theme(other)
            reloaded = ThemeManager(None, cache_dir=tmp)
            self.assertEqual(reloaded.current_theme(), other)

    def test_corrupt_persisted_theme_falls_back_to_default(self):
        with tempfile.TemporaryDirectory() as tmp:
            with open(os.path.join(tmp, "theme.txt"), "w", encoding="utf-8") as f:
                f.write("no-such-theme\n")
            manager = ThemeManager(None, cache_dir=tmp)
            self.assertEqual(manager.current_theme(), DEFAULT_THEME)

    def test_preview_overrides_active_until_cleared(self):
        with tempfile.TemporaryDirectory() as tmp:
            manager = ThemeManager(None, cache_dir=tmp)
            other = _pick_non_default_theme()
            self.assertTrue(manager.preview(other))
            self.assertEqual(manager.active_theme(), other)
            self.assertEqual(manager.current_theme(), DEFAULT_THEME)
            self.assertTrue(manager.clear_preview())
            self.assertEqual(manager.active_theme(), DEFAULT_THEME)

    def test_preview_invalid_name_reverts_to_committed(self):
        with tempfile.TemporaryDirectory() as tmp:
            manager = ThemeManager(None, cache_dir=tmp)
            other = _pick_non_default_theme()
            manager.preview(other)
            self.assertTrue(manager.preview("dracul"))
            self.assertEqual(manager.active_theme(), DEFAULT_THEME)

    def test_preview_same_name_reports_no_change(self):
        with tempfile.TemporaryDirectory() as tmp:
            manager = ThemeManager(None, cache_dir=tmp)
            other = _pick_non_default_theme()
            self.assertTrue(manager.preview(other))
            self.assertFalse(manager.preview(other))
            self.assertTrue(manager.clear_preview())
            self.assertFalse(manager.clear_preview())

    def test_set_theme_clears_preview(self):
        with tempfile.TemporaryDirectory() as tmp:
            manager = ThemeManager(None, cache_dir=tmp)
            other = _pick_non_default_theme()
            manager.preview(other)
            self.assertTrue(manager.set_theme(other))
            self.assertEqual(manager.active_theme(), other)
            self.assertFalse(manager.clear_preview())

    def test_active_style_differs_between_themes_and_is_cached(self):
        with tempfile.TemporaryDirectory() as tmp:
            manager = ThemeManager(None, cache_dir=tmp)
            default_style = manager.active_style()
            self.assertIs(manager.active_style(), default_style)
            manager.preview(_pick_non_default_theme())
            self.assertIsNot(manager.active_style(), default_style)


class ThemeCompleterTests(unittest.TestCase):
    def _completions(self, text):
        completer = ThemeCompleter(list_themes)
        document = Document(text, len(text))
        return [c.text for c in completer.get_completions(document, CompleteEvent())]

    def test_lists_all_themes_after_theme_prefix(self):
        self.assertEqual(self._completions("/theme "), list(list_themes()))

    def test_filters_by_typed_prefix(self):
        results = self._completions("/theme ma")
        self.assertIn(DEFAULT_THEME, results)
        self.assertTrue(all(name.startswith("ma") for name in results))

    def test_ignores_non_theme_input(self):
        self.assertEqual(self._completions("/dofile "), [])
        self.assertEqual(self._completions("Console."), [])

    def test_ignores_extra_argument(self):
        self.assertEqual(self._completions("/theme dracula "), [])


class BuiltinCmdCompleterArgumentStageTests(unittest.TestCase):
    def test_command_completions_stop_after_space(self):
        completer = BuiltinCmdCompleter(repl.builtin_cmds, repl._builtin_command_order)
        document = Document("/theme ", len("/theme "))
        self.assertEqual(list(completer.get_completions(document, CompleteEvent())), [])


class ThemeCompletionTriggerTests(unittest.TestCase):
    def test_theme_argument_stage_triggers_completion(self):
        buff = FakeThemeBuffer("/theme dr")
        trigger_completion_on_change(buff, lambda: True)
        self.assertTrue(buff.started_completion)

    def test_other_command_argument_stage_does_not_trigger(self):
        buff = FakeThemeBuffer("/dofile some")
        trigger_completion_on_change(buff, lambda: True)
        self.assertFalse(buff.started_completion)


class ThemeBuiltinTests(unittest.TestCase):
    def setUp(self):
        self._original_manager = repl.theme_manager
        self._tmp = tempfile.TemporaryDirectory()
        repl.theme_manager = ThemeManager(None, cache_dir=self._tmp.name)

    def tearDown(self):
        repl.theme_manager = self._original_manager
        self._tmp.cleanup()

    def _run_theme(self, argument):
        output_buffer = io.StringIO()
        with redirect_stdout(output_buffer):
            repl.builtin_cmds["/theme"]["func"](argument)
        return output_buffer.getvalue()

    def test_theme_command_is_registered(self):
        self.assertIn("/theme", repl.builtin_cmds)
        self.assertIn("/theme", repl._builtin_command_order)

    def test_no_argument_lists_current_and_candidates(self):
        output = self._run_theme("")
        self.assertIn(f"Current theme: {DEFAULT_THEME}", output)
        self.assertIn("Available themes", output)
        self.assertIn(DEFAULT_THEME, output)

    def test_valid_argument_switches_theme(self):
        other = _pick_non_default_theme()
        output = self._run_theme(other)
        self.assertIn(f"Theme switched to '{other}'", output)
        self.assertEqual(repl.theme_manager.current_theme(), other)

    def test_invalid_argument_reports_error(self):
        output = self._run_theme("no-such-theme")
        self.assertIn("Unknown theme: no-such-theme", output)
        self.assertEqual(repl.theme_manager.current_theme(), DEFAULT_THEME)


class ThemePreviewOnTextChangeTests(unittest.TestCase):
    def setUp(self):
        self._original_manager = repl.theme_manager
        self._tmp = tempfile.TemporaryDirectory()
        repl.theme_manager = ThemeManager(None, cache_dir=self._tmp.name)

    def tearDown(self):
        repl.theme_manager = self._original_manager
        self._tmp.cleanup()

    def test_full_theme_name_previews_live(self):
        other = _pick_non_default_theme()
        repl._handle_theme_preview_on_change(FakeThemeBuffer(f"/theme {other}"))
        self.assertEqual(repl.theme_manager.active_theme(), other)
        self.assertEqual(repl.theme_manager.current_theme(), DEFAULT_THEME)

    def test_partial_name_reverts_preview(self):
        other = _pick_non_default_theme()
        repl._handle_theme_preview_on_change(FakeThemeBuffer(f"/theme {other}"))
        repl._handle_theme_preview_on_change(FakeThemeBuffer("/theme dracul"))
        self.assertEqual(repl.theme_manager.active_theme(), DEFAULT_THEME)

    def test_clearing_input_reverts_preview(self):
        other = _pick_non_default_theme()
        repl._handle_theme_preview_on_change(FakeThemeBuffer(f"/theme {other}"))
        repl._handle_theme_preview_on_change(FakeThemeBuffer(""))
        self.assertEqual(repl.theme_manager.active_theme(), DEFAULT_THEME)


if __name__ == "__main__":
    unittest.main(verbosity=2)
