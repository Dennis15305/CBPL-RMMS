USE [RMMS_Demo];
GO

SET XACT_ABORT ON;
GO

IF OBJECT_ID(N'[dbo].[RMMS_EventSink]', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[RMMS_EventSink]
    (
        [EventTime] datetime2(3) NOT NULL,
        [LineName] nvarchar(100) NOT NULL,
        [OrderNumber] nvarchar(50) NOT NULL,
        [ShiftNumber] nvarchar(20) NOT NULL,
        [StateCode] nvarchar(20) NOT NULL,
        [OrderLength] decimal(18, 1) NOT NULL,
        [OrderArea] decimal(18, 2) NOT NULL,
        [LineSpeed] decimal(18, 1) NOT NULL,
        [OperatorNumber] nvarchar(20) NOT NULL,
        [ChangeoverMinutes] decimal(18, 1) NOT NULL,
        [AlarmBits] nvarchar(20) NOT NULL
    );
END;
GO

IF OBJECT_ID(N'[dbo].[RMMS_EventReceipt]', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[RMMS_EventReceipt]
    (
        [ControllerCode] nvarchar(50) NOT NULL,
        [SourceEpoch] int NOT NULL,
        [PlcEventId] bigint NOT NULL,
        [PlcUnixTime] bigint NOT NULL,
        [PayloadFingerprint] varbinary(32) NOT NULL,
        [InsertedAtUtc] datetime2(3) NOT NULL,
        CONSTRAINT [PK_RMMS_EventReceipt]
            PRIMARY KEY CLUSTERED ([ControllerCode], [SourceEpoch], [PlcEventId]),
        CONSTRAINT [CK_RMMS_EventReceipt_SourceEpoch]
            CHECK ([SourceEpoch] > 0),
        CONSTRAINT [CK_RMMS_EventReceipt_EventId]
            CHECK ([PlcEventId] > 0)
    );
END;
GO

SELECT
    [ControllerCode], [SourceEpoch], [PlcEventId], [PlcUnixTime],
    [InsertedAtUtc]
FROM [dbo].[RMMS_EventReceipt]
ORDER BY [InsertedAtUtc] DESC;
GO
