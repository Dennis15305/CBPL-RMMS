USE [RMMS_Demo];
GO

SELECT
    RollNumber,
    IsConfigured,
    IsActive,
    CurrentLength,
    PreviousLength,
    SpeedMetersPerMinute,
    SpliceSignal,
    ResetSignal,
    InputError,
    UpdatedAtUtc
FROM [rmms].[vRollLiveState]
ORDER BY RollNumber;

SELECT
    CollectorName,
    PlcOnline,
    IsOnlineNow,
    PlcQueueCount,
    PlcOverflowCount,
    PlcHeartbeat,
    LastPollAtUtc,
    LastEventAtUtc,
    LastError,
    UpdatedAtUtc
FROM [rmms].[vCollectorStatus];
GO
