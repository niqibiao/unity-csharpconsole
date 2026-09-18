import os
import tempfile
import unittest

import _bootstrap  # noqa: F401
from csharpconsole_core.runtime_artifacts_base import prepare_runtime_artifacts, read_compile_set, zip_directory


class RuntimeArtifactsBaseTests(unittest.TestCase):
    def test_zip_directory_includes_extra_file_outside_root(self):
        with tempfile.TemporaryDirectory() as root, tempfile.NamedTemporaryFile(delete=False) as extra:
            try:
                with open(os.path.join(root, 'a.txt'), 'w', encoding='utf-8') as f:
                    f.write('hello')
                extra.write(b'defines')
                extra.close()
                data = zip_directory(root, extra.name, 'runtime-defines.txt')
                self.assertTrue(len(data) > 0)
            finally:
                os.unlink(extra.name)

    def test_read_compile_set_reads_a_local_zip(self):
        with tempfile.TemporaryDirectory() as root:
            path = os.path.join(root, 'CSharpConsoleCompileSet.zip')
            with open(path, 'wb') as f:
                f.write(b'zip-bytes')
            self.assertEqual(read_compile_set(path, fetch_bytes=None), b'zip-bytes')

    def test_read_compile_set_downloads_a_url(self):
        fetched = []
        data = read_compile_set('https://builds.example/set.zip', lambda url: fetched.append(url) or b'remote')
        self.assertEqual(data, b'remote')
        self.assertEqual(fetched, ['https://builds.example/set.zip'])

    def test_read_compile_set_reports_a_missing_file(self):
        with tempfile.TemporaryDirectory() as root:
            with self.assertRaises(FileNotFoundError):
                read_compile_set(os.path.join(root, 'missing.zip'), fetch_bytes=None)

    def test_prepare_runtime_artifacts_editor_mode_short_circuits(self):
        result = prepare_runtime_artifacts(False, '', '', 'runtime-defines.txt', None, None, None, 'runtimeDefinesPath')
        self.assertTrue(result['ok'])
        self.assertEqual(result['mode'], 'editor')


if __name__ == '__main__':
    unittest.main()
