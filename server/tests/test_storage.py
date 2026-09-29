from pathlib import Path
import tempfile
import unittest

from cbpl_collector.protocol import parse_event_packet
from cbpl_collector.storage import EventStorage
from test_protocol import TEST_VECTOR


class StorageTests(unittest.TestCase):
    def test_event_id_is_idempotent(self) -> None:
        project_dir = Path(__file__).resolve().parent.parent
        with tempfile.TemporaryDirectory() as temp_dir:
            storage = EventStorage(
                Path(temp_dir) / "events.db", project_dir / "schema.sql"
            )
            storage.initialize()
            event = parse_event_packet(TEST_VECTOR)

            self.assertTrue(storage.save_event(event))
            self.assertFalse(storage.save_event(event))
            self.assertTrue(storage.has_event(17))
            self.assertEqual(storage.pending_central_count(), 1)


if __name__ == "__main__":
    unittest.main()

