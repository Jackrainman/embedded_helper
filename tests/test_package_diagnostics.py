import importlib.util
import json
from pathlib import Path
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('package_client', ROOT / 'scripts/package_client.py')
package = importlib.util.module_from_spec(spec)
spec.loader.exec_module(package)


class PackageDiagnosticsTests(unittest.TestCase):
    def test_runtime_directory_requires_exactly_one_match(self):
        with tempfile.TemporaryDirectory(dir=ROOT / 'tmp') as temp:
            root = Path(temp)
            for phase in ['首次启动', '搬迁启动']:
                with self.assertRaisesRegex(RuntimeError, phase):
                    package.find_runtime(root, '1.0.0', phase)
            first = root / 'data/runtime/1.0.0-one'
            first.mkdir(parents=True)
            self.assertEqual(package.find_runtime(root, '1.0.0', '首次启动'), first)
            (first.parent / '1.0.0-two').mkdir()
            with self.assertRaisesRegex(RuntimeError, '2'):
                package.find_runtime(root, '1.0.0', '搬迁启动')

    def test_runtime_comes_from_publish_and_must_match_authority(self):
        with tempfile.TemporaryDirectory(dir=ROOT / 'tmp') as temp:
            root = Path(temp)
            props = root / 'Directory.Build.props'
            props.write_text('<Project><PropertyGroup><RuntimeFrameworkVersion>10.0.12</RuntimeFrameworkVersion></PropertyGroup></Project>')
            for name in ['EmbeddedHelper', 'EmbeddedHelper.Worker', 'embedded-helper-cli']:
                (root / (name + '.runtimeconfig.json')).write_text(json.dumps({'runtimeOptions': {'includedFrameworks': [{'name': 'Microsoft.NETCore.App', 'version': '10.0.12'}]}}))
            self.assertEqual(package.published_runtime(root, props), '10.0.12')
            config = root / 'EmbeddedHelper.runtimeconfig.json'
            config.write_text(config.read_text().replace('10.0.12', '10.0.13'))
            with self.assertRaisesRegex(ValueError, 'EmbeddedHelper.runtimeconfig.json'):
                package.published_runtime(root, props)
