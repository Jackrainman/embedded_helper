import json
import os
from pathlib import Path
import shutil
import struct
import sys
import tempfile
import threading
import unittest
from unittest.mock import Mock

import serial

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))
from prototypes.observation import ReadPlan, decode, index_elf
from prototypes.serial_record import Recorder


class ObservationTests(unittest.TestCase):
    def test_real_arm_dwarf_and_rejections(self):
        index = {v['name']: v for v in index_elf(ROOT / 'tests/fixtures/scalars.elf').values()}
        for name, fmt, value in [('signed8', 'b', -8), ('unsigned8', 'B', 8), ('signed16', 'h', -16),
                                  ('unsigned16', 'H', 16), ('signed32', 'i', -32),
                                  ('unsigned32', 'I', 32), ('float32', 'f', 1.25)]:
            self.assertEqual(decode(index[name], struct.pack('<' + fmt, value)), value)
        self.assertFalse(index['unsupported64']['supported'])
        with self.assertRaises(ValueError):
            decode(index['signed32'], b'\0')
        with self.assertRaises(Exception):
            index_elf(ROOT / 'tests/fixtures/invalid.elf')

    def test_reconnect_keeps_plan_and_failed_update_is_atomic(self):
        with tempfile.TemporaryDirectory(dir=ROOT / 'tmp') as temp:
            path = Path(temp) / '中文 固件.elf'
            shutil.copy2(ROOT / 'tests/fixtures/scalars.elf', path)
            plan = ReadPlan(path)
            old = plan.items
            os.utime(path, ns=(plan.mtime - 10000000, plan.mtime - 10000000))
            self.assertTrue(plan.changed())
            self.assertIs(plan.reconnect(), old)
            path.write_bytes(b'half written')
            with self.assertRaises(Exception):
                plan.update()
            self.assertIs(plan.items, old)

    def test_bidirectional_order_segments_and_all_bytes(self):
        with tempfile.TemporaryDirectory(dir=ROOT / 'tmp') as temp:
            directory = Path(temp) / 'records'
            recorder = Recorder(directory, 600)
            data = (ROOT / 'tests/fixtures/serial.bin').read_bytes()
            expected = {'TX': b'', 'RX': b''}
            for offset in range(0, len(data), 7):
                for direction in expected:
                    block = data[offset:offset + 7]
                    recorder.append(direction, block)
                    expected[direction] += block
            recorder.close()
            records = [json.loads(line) for p in sorted(directory.glob('*.jsonl')) for line in p.read_text().splitlines()]
            self.assertGreater(len(list(directory.glob('*.jsonl'))), 1)
            self.assertEqual([r['sequence'] for r in records], list(range(len(records))))
            for direction in expected:
                self.assertEqual(b''.join(bytes.fromhex(r['hex']) for r in records if r['direction'] == direction), data)

    def test_loopback_and_disk_failure_are_not_silent(self):
        payload = (ROOT / 'tests/fixtures/serial.bin').read_bytes()
        with serial.serial_for_url('loop://', baudrate=1000000, timeout=1) as port:
            self.assertEqual(port.write(payload), len(payload))
            self.assertEqual(port.read(len(payload)), payload)
        with tempfile.TemporaryDirectory(dir=ROOT / 'tmp') as temp:
            recorder = Recorder(Path(temp) / 'records')
            recorder.file = Mock()
            recorder.file.write.side_effect = OSError('disk full')
            with self.assertRaises(OSError):
                recorder.append('RX', b'bytes')
            self.assertTrue(recorder.failed)
            self.assertEqual(recorder.sequence, 0)
            with self.assertRaises(OSError):
                recorder.append('TX', b'more')
            recorder.close()

    def test_concurrent_directions_share_one_sequence(self):
        with tempfile.TemporaryDirectory(dir=ROOT / 'tmp') as temp:
            directory = Path(temp) / 'records'
            recorder = Recorder(directory, 100000)
            def send(direction):
                for i in range(100):
                    recorder.append(direction, bytes([i]))
            threads = [threading.Thread(target=send, args=(direction,)) for direction in ['TX', 'RX']]
            for thread in threads:
                thread.start()
            for thread in threads:
                thread.join()
            recorder.close()
            records = [json.loads(line) for p in sorted(directory.glob('*.jsonl')) for line in p.read_text().splitlines()]
            self.assertEqual([r['sequence'] for r in records], list(range(200)))
            for direction in ['TX', 'RX']:
                self.assertEqual(b''.join(bytes.fromhex(r['hex']) for r in records if r['direction'] == direction), bytes(range(100)))
