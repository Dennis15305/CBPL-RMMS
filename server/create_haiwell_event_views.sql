USE [RMMS_Demo];
GO

SET NOCOUNT ON;
SET XACT_ABORT ON;
GO

IF OBJECT_ID(N'[rmms].[vRollEvents]', N'V') IS NULL
    THROW 51020, N'View [rmms].[vRollEvents] was not found.', 1;
GO

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'[rmms].[RollEvents]', N'U')
      AND name = N'IX_rmms_RollEvents_RollNumber_EventTimeUtc'
)
BEGIN
    CREATE INDEX [IX_rmms_RollEvents_RollNumber_EventTimeUtc]
        ON [rmms].[RollEvents] ([RollNumber], [EventTimeUtc] DESC)
        INCLUDE ([Id], [LengthMm], [EventType], [ShiftNumber]);
END;
GO

/* Operator-facing history: event time is returned in Moscow local time. */
CREATE OR ALTER VIEW [rmms].[vHaiwellRollEvents]
AS
    SELECT
        events.Id,
        events.RollNumber,
        events.Length,
        events.EventType,
        events.ShiftNumber,
        DATEADD(HOUR, 3, events.EventTimeUtc) AS EventTime
    FROM [rmms].[vRollEvents] AS events;
GO

/*
    Events belonging to the current production shift in Moscow time:
      shift 1: 08:00 inclusive to 20:00 exclusive;
      shift 2: 20:00 inclusive to 08:00 exclusive on the next day.

    RollNumber is deliberately not fixed here. Haiwell selects the required
    unwind with WHERE RollNumber = <selected roll> and supplies ORDER BY.
*/
CREATE OR ALTER VIEW [rmms].[vRollEventsCurrentShift]
AS
    WITH CurrentClock AS
    (
        SELECT DATEADD(HOUR, 3, CONVERT(datetime2(0), SYSUTCDATETIME())) AS NowLocal
    ),
    LocalShiftBounds AS
    (
        SELECT
            CASE
                WHEN CONVERT(time, NowLocal) >= CONVERT(time, '08:00')
                 AND CONVERT(time, NowLocal) <  CONVERT(time, '20:00')
                    THEN DATEADD(HOUR, 8,
                         CONVERT(datetime2(0), CONVERT(date, NowLocal)))
                WHEN CONVERT(time, NowLocal) >= CONVERT(time, '20:00')
                    THEN DATEADD(HOUR, 20,
                         CONVERT(datetime2(0), CONVERT(date, NowLocal)))
                ELSE DATEADD(HOUR, 20,
                         CONVERT(datetime2(0),
                             DATEADD(DAY, -1, CONVERT(date, NowLocal))))
            END AS ShiftStartLocal
        FROM CurrentClock
    ),
    UtcShiftBounds AS
    (
        SELECT
            DATEADD(HOUR, -3, ShiftStartLocal) AS ShiftStartUtc,
            DATEADD(HOUR,  9, ShiftStartLocal) AS ShiftEndUtc
        FROM LocalShiftBounds
    )
    SELECT
        events.Id,
        events.RollNumber,
        events.Length,
        events.EventType,
        events.ShiftNumber,
        DATEADD(HOUR, 3, events.EventTimeUtc) AS EventTime
    FROM [rmms].[vRollEvents] AS events
    CROSS JOIN UtcShiftBounds AS bounds
    WHERE events.EventTimeUtc >= bounds.ShiftStartUtc
      AND events.EventTimeUtc <  bounds.ShiftEndUtc;
GO

IF OBJECT_ID(N'[rmms].[vRollEventsCurrentShift]', N'V') IS NULL
    THROW 51021, N'View [rmms].[vRollEventsCurrentShift] was not created.', 1;

IF OBJECT_ID(N'[rmms].[vHaiwellRollEvents]', N'V') IS NULL
    THROW 51022, N'View [rmms].[vHaiwellRollEvents] was not created.', 1;

-- Verification sample: current-shift events for unwind 1, newest first.
SELECT
    [Id],
    [RollNumber],
    [Length],
    [EventType],
    [ShiftNumber],
    [EventTime]
FROM [rmms].[vRollEventsCurrentShift]
WHERE [RollNumber] = 1
ORDER BY [Id] DESC;
GO
