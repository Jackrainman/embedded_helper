from pathlib import Path
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from prototypes.worker import Session


class WorkerTests(unittest.TestCase):
    def test_normal_stop_and_blocked_worker_cleanup_before_retry(self):
        session = Session()
        self.addCleanup(session.stop)
        for block in [False, True, False]:
            session.start(block)
            self.assertEqual(session.state, 'connected')
            with self.assertRaises(RuntimeError):
                session.start()
            session.stop()
            self.assertEqual(session.state, 'disconnected')
            self.assertIsNone(session.process)
        self.assertEqual(session.generation, 3)
