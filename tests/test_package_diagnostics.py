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
