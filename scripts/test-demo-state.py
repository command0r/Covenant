import importlib.util
from pathlib import Path
import tempfile
import unittest

spec = importlib.util.spec_from_file_location('demo_state', Path(__file__).with_name('demo-state.py'))
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class MigrationTests(unittest.TestCase):
    def test_preserves_history_and_source_and_never_overwrites_destination(self):
        with tempfile.TemporaryDirectory() as root:
            old, new = Path(root) / 'old', Path(root) / 'new'
            (old / 'audit.log.config').mkdir(parents=True)
            (old / 'audit.log.config/active.json').write_text('{"version":7}')
            (old / 'audit.log').write_text('evidence')
            self.assertTrue(module.migrate(old, new))
            self.assertEqual((new / 'audit.log').read_text(), 'evidence')
            self.assertTrue((old / 'audit.log').exists())
            (old / 'audit.log').write_text('later evidence')
            self.assertFalse(module.migrate(old, new))
            self.assertEqual((new / 'audit.log').read_text(), 'evidence')
            self.assertEqual(new.stat().st_mode & 0o777, 0o700)

    def test_absent_or_incomplete_legacy_state_is_not_imported(self):
        with tempfile.TemporaryDirectory() as root:
            old, new = Path(root) / 'old', Path(root) / 'new'
            self.assertFalse(module.migrate(old, new))
            old.mkdir()
            self.assertFalse(module.migrate(old, new))
            self.assertFalse(new.exists())


if __name__ == '__main__':
    unittest.main()
