import importlib.util
import json
from pathlib import Path
import tempfile
import unittest

spec = importlib.util.spec_from_file_location('demo_credentials', Path(__file__).with_name('demo-credentials.py'))
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class CredentialEnvironmentTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.path = Path(self.temp.name) / 'secrets.json'
        self.path.write_text(json.dumps({'Anthropic:ApiKey': 'test-anthropic', 'Tavily:ApiKey': 'test-tavily', 'Other:Secret': 'must-not-load'}))

    def test_only_selected_keys_loaded(self):
        env = module.host_environment({}, self.path)
        self.assertEqual(env['Anthropic__ApiKey'], 'test-anthropic')
        self.assertEqual(env['Tavily__ApiKey'], 'test-tavily')
        self.assertNotIn('Other__Secret', env)
        self.assertIn('api.anthropic.com:443', env['Providers__ProbeAllowList'])

    def test_environment_and_explicit_allowlist_win(self):
        source = {'Anthropic__ApiKey': 'override', 'Tavily__ApiKey': '', 'Providers__ProbeAllowList': 'localhost:11434'}
        env = module.host_environment(source, self.path)
        self.assertEqual(env, source)

    def test_opt_out_does_not_load_keys(self):
        env = module.host_environment({'LOAD_SAVED_KEYS': '0'}, self.path)
        self.assertNotIn('Anthropic__ApiKey', env)
        self.assertNotIn('Tavily__ApiKey', env)

    def test_absent_store_allows_local_demo(self):
        env = module.host_environment({}, self.path.with_name('missing.json'))
        self.assertNotIn('Anthropic__ApiKey', env)

    def test_malformed_store_fails_without_echoing_content(self):
        self.path.write_text('private-sentinel-not-json')
        with self.assertRaises(ValueError) as error:
            module.host_environment({}, self.path)
        self.assertNotIn('private-sentinel', str(error.exception))

    def test_bom_and_local_authority(self):
        self.path.write_text('\ufeff' + self.path.read_text())
        env = module.host_environment({'Providers__LocalEndpoints': 'workstation:11434'}, self.path)
        self.assertTrue(env['Providers__ProbeAllowList'].startswith('workstation:11434,'))


if __name__ == '__main__':
    unittest.main()
