import unittest

import _bootstrap  # noqa: F401
from csharpconsole_core.models import make_result, new_run_id


class ModelsTests(unittest.TestCase):
    def test_make_result_populates_expected_fields(self):
        result = make_result(True, "execute", "ok", 0, "done", "sid-1", "editor", run_id="run-1", duration_ms=12.7, data={"x": 1})
        self.assertEqual(result["ok"], True)
        self.assertEqual(result["stage"], "execute")
        self.assertEqual(result["type"], "ok")
        self.assertEqual(result["exitCode"], 0)
        self.assertEqual(result["summary"], "done")
        self.assertEqual(result["sessionId"], "sid-1")
        self.assertEqual(result["mode"], "editor")
        self.assertEqual(result["runId"], "run-1")
        self.assertEqual(result["durationMs"], 12)
        self.assertEqual(result["data"], {"x": 1})

    def test_new_run_id_has_timestamp_prefix(self):
        run_id = new_run_id()
        self.assertRegex(run_id, r"^\d{8}-\d{6}-[0-9a-f]{8}$")


if __name__ == "__main__":
    unittest.main()
