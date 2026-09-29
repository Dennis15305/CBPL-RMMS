# Implementation status

This document separates verified work from design-stage work.

## Verified

- PLCopenXML imported into EcoStruxure Machine Expert 2.3.
- PLC project built with no errors or warnings.
- `SR_Main` assigned to MAST and loaded to a running TM241.
- Persistent roll-event and stop-journal queues implemented.
- C# collector, SQLite storage, SQL synchronization and automated tests created.
- Collector update deployed while preserving the existing local database.
- SQL duplicate-protection table created and checked.
- New Weintek command sequence observed by the PLC during commissioning.

## Prepared, final site record pending

- Full commissioning of five DWIN panels on one RS-485 trunk.
- Long-duration validation and archived export of the Haiwell project.
- Full regression of all updated Weintek macros.
- Final cutover from legacy direct SQL inserts to the PLC stop journal.
- Six-channel mechanical installation and metrological comparison.

## Design stage

- Automatic splice command based on direct paper-web break detection.
- Ten through-beam optical channels and their mechanical protection.
- Final multidimensional material-loss model.

