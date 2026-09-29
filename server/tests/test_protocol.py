import unittest

from cbpl_collector.protocol import (
    ProtocolError,
    RollEvent,
    RollEndReason,
    crc16_modbus_words,
    parse_event_packet,
    split_u32_hi_lo,
)


TEST_VECTOR = [
    0xCB01,
    0x0002,
    0x0000,
    0x0011,
    0x6A8E,
    0xC154,
    0x0003,
    0x0002,
    0x0001,
    0x0001,
    0xE208,
    0x41AE,
]


class ProtocolTests(unittest.TestCase):
    def test_official_protocol_v2_vector(self) -> None:
        self.assertEqual(crc16_modbus_words(TEST_VECTOR[:11]), 0x41AE)
        event = parse_event_packet(TEST_VECTOR)
        self.assertEqual(event.event_id, 17)
        self.assertEqual(event.timestamp_utc, 1_787_740_500)
        self.assertEqual(event.unwind_number, 3)
        self.assertEqual(event.shift_number, 2)
        self.assertEqual(event.end_reason, RollEndReason.SPLICE)
        self.assertEqual(event.length_mm, 123_400)
        self.assertTrue(event.timestamp_valid)

    def test_crc_mismatch_is_rejected(self) -> None:
        damaged = TEST_VECTOR.copy()
        damaged[10] ^= 1
        with self.assertRaisesRegex(ProtocolError, "CRC mismatch"):
            parse_event_packet(damaged)

    def test_u32_ack_word_order_is_high_then_low(self) -> None:
        self.assertEqual(split_u32_hi_lo(0x12345678), [0x1234, 0x5678])

    def test_obviously_old_rtc_value_is_marked_invalid(self) -> None:
        event = RollEvent(
            event_id=1,
            timestamp_utc=1_160_723_838,
            unwind_number=1,
            shift_number=1,
            end_reason=RollEndReason.RESET,
            length_mm=109_741,
            raw_words=tuple(TEST_VECTOR),
        )
        self.assertFalse(event.timestamp_valid)


if __name__ == "__main__":
    unittest.main()
