from io import StringIO
import json
from pathlib import Path
import shutil
import sys
import tempfile
import unittest

from ruamel.yaml import YAML

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))
from prototypes.project_config import read_local


class ConfigTests(unittest.TestCase):
    def test_local_sources_and_unknown_yaml_roundtrip(self):
        source = ROOT / 'tests/fixtures/G4 中文 空格'
        model = read_local(source)
        self.assertEqual(model['device'], 'STM32G474VE')
        self.assertEqual(model['mcu'], 'STM32G474VETx')
        self.assertEqual(model['speedKHz'], 8000)
        self.assertEqual(model['interface'], 'SWD')
        yaml = YAML()
        data = yaml.load((source / '.eide/eide.yml').read_text(encoding='utf-8'))
        data['targets']['Debug']['uploadConfigMap']['JLink']['speed'] = 4000
        output = StringIO()
        yaml.dump(data, output)
        self.assertIn('# Unknown extension must survive edits', output.getvalue())
        self.assertEqual(yaml.load(output.getvalue())['x-fixture'], 'preserve-me')

    def test_no_remote_required_and_conflicts_fail(self):
        with tempfile.TemporaryDirectory(dir=ROOT / 'tmp') as temp:
            root = Path(temp) / '工程'
            shutil.copytree(ROOT / 'tests/fixtures/G4 中文 空格', root)
            (root / 'remote-jlink.json').unlink()
            self.assertEqual(read_local(root)['device'], 'STM32G474VE')
            p = root / '.vscode/launch.json'
            data = json.loads(p.read_text(encoding='utf-8'))
            data['configurations'][0]['device'] = 'STM32F407VG'
            p.write_text(json.dumps(data), encoding='utf-8')
            with self.assertRaises(ValueError):
                read_local(root)
            self.assertFalse((root / 'remote-jlink.json').exists())
