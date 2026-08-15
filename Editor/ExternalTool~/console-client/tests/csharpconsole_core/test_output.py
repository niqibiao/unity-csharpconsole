import io
import unittest
from contextlib import redirect_stdout

import _bootstrap  # noqa: F401
from csharpconsole_core.output import emit_result, print_text_result, render_text_result


class OutputTests(unittest.TestCase):
    def test_render_text_result_normalizes_escaped_whitespace(self):
        rendered = render_text_result({"data": {"text": "a\\nb\\t1"}, "summary": ""}, lambda data: data.get("text", ""))
        self.assertEqual(rendered, "a\nb\t1")

    def test_render_text_result_preserves_explicit_empty_text(self):
        rendered = render_text_result({"data": {"text": ""}, "summary": "OK"}, lambda data: data["text"] if "text" in data else None)
        self.assertEqual(rendered, "")

    def test_emit_result_outputs_json(self):
        stream = io.StringIO()
        with redirect_stdout(stream):
            emit_result({"ok": True}, as_json=True, print_text=lambda _result: None)
        self.assertIn('"ok": true', stream.getvalue().lower())

    def test_print_text_result_uses_summary_on_error(self):
        stream = io.StringIO()
        with redirect_stdout(stream):
            print_text_result({"ok": False, "summary": "boom", "data": {}}, lambda data: data.get("text", ""))
        self.assertIn("boom", stream.getvalue())


if __name__ == "__main__":
    unittest.main()
