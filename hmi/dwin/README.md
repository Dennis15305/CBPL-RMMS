# DWIN local HMI

Five `DMG80480T070_A5WTR` panels are used as Modbus RTU slaves with addresses
1...5. The Schneider TM241 is the RS-485 master.

The display package contains current and previous roll length, physical unwind
number, shift and RTC state. System VP `009C` is used to synchronize the panel
clock from the PLC.

The binary project artifacts are supplied for technical review. Validate one
panel on a bench before loading all field devices.

