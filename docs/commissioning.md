# Commissioning checklist

1. Back up PLC, HMI and server projects.
2. Verify PNP/NPN levels, COM, 0 V, PE and cable shields.
3. Commission one complete metering channel before replication.
4. Verify the scale against a known material length: 1 pulse = 1 mm.
5. Test every HSC, `Splice`, short `Reset` and completed-roll `Reset`.
6. Test retained-state recovery after a controlled power cycle.
7. Verify DWIN slave address, 32-bit word order, RTC and RS-485 termination.
8. Verify Haiwell scaling, invalid-data display and loss-of-link behaviour.
9. Verify Weintek command sequence and acknowledgement.
10. Never run legacy and new SQL writers for the same event simultaneously.
11. Interrupt network and SQL access and confirm no event is lost or duplicated.
12. Remove all forces before production use.

Automatic splice commissioning requires a bench test, dry run and supervised
low-speed production trial before normal operation.

