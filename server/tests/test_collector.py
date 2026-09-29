from pathlib import Path
import tempfile
import unittest

from cbpl_collector.collector import Collector, CycleResult
from cbpl_collector.config import PlcConfig
from cbpl_collector.protocol import ACK_COMMAND, ACK_COMMAND_ADDRESS, ACK_EVENT_ID_ADDRESS
from cbpl_collector.storage import EventStorage
from test_protocol import TEST_VECTOR


class FakeModbusClient:
    def __init__(self) -> None:
        self.writes: list[tuple[str, int, object]] = []
        self.read_count = 0

    def read_holding_registers(self, address: int, count: int) -> list[int]:
        self.read_count += 1
        if count == 16:
            return TEST_VECTOR + [1, 1, 128, 1]
        if count == 21:
            verification = [0] * 21
            verification[12] = 0
            verification[18] = 0
            verification[20] = 1
            return verification
        raise AssertionError(f"unexpected read: address={address}, count={count}")

    def write_single_register(self, address: int, value: int) -> None:
        self.writes.append(("single", address, value))

    def write_multiple_registers(self, address: int, values: list[int]) -> None:
        self.writes.append(("multiple", address, list(values)))


class CollectorTests(unittest.TestCase):
    def _storage(self, directory: str) -> EventStorage:
        project_dir = Path(__file__).resolve().parent.parent
        storage = EventStorage(
            Path(directory) / "events.db", project_dir / "schema.sql"
        )
        storage.initialize()
        return storage

    def test_read_only_mode_never_writes_to_plc(self) -> None:
        with tempfile.TemporaryDirectory() as temp_dir:
            client = FakeModbusClient()
            storage = self._storage(temp_dir)
            collector = Collector(
                PlcConfig(host="127.0.0.1", enable_writes=False), client, storage
            )

            result = collector.collect_once()

            self.assertEqual(result, CycleResult.STORED_READ_ONLY)
            self.assertTrue(storage.has_event(17))
            self.assertEqual(client.writes, [])

    def test_ack_words_are_written_before_ack_command(self) -> None:
        with tempfile.TemporaryDirectory() as temp_dir:
            client = FakeModbusClient()
            storage = self._storage(temp_dir)
            collector = Collector(
                PlcConfig(
                    host="127.0.0.1",
                    enable_writes=True,
                    ack_verify_timeout_seconds=0.2,
                ),
                client,
                storage,
            )

            result = collector.collect_once()

            self.assertEqual(result, CycleResult.ACKNOWLEDGED)
            ack_writes = [write for write in client.writes if write[1] != 2019]
            self.assertEqual(
                ack_writes,
                [
                    ("multiple", ACK_EVENT_ID_ADDRESS, [0, 17]),
                    ("single", ACK_COMMAND_ADDRESS, ACK_COMMAND),
                ],
            )


if __name__ == "__main__":
    unittest.main()

