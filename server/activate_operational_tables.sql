USE [RMMS_Demo];
GO

SET NOCOUNT ON;
SET XACT_ABORT ON;

IF OBJECT_ID(N'[rmms].[RollLiveState]', N'U') IS NULL
    THROW 51000, N'Table [rmms].[RollLiveState] was not found.', 1;

IF OBJECT_ID(N'[rmms].[CollectorStatus]', N'U') IS NULL
    THROW 51001, N'Table [rmms].[CollectorStatus] was not found.', 1;

BEGIN TRY
    BEGIN TRANSACTION;

    -- Старые CHECK-ограничения зависят от переименовываемого столбца.
    -- Таблица имеет только две штатные проверки; ниже они создаются заново.
    DECLARE @ConstraintName sysname;
    DECLARE @DropSql nvarchar(max);
    DECLARE CheckConstraints CURSOR LOCAL FAST_FORWARD FOR
        SELECT name
        FROM sys.check_constraints
        WHERE parent_object_id = OBJECT_ID(N'[rmms].[RollLiveState]');

    OPEN CheckConstraints;
    FETCH NEXT FROM CheckConstraints INTO @ConstraintName;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        SET @DropSql = N'ALTER TABLE [rmms].[RollLiveState] DROP CONSTRAINT '
            + QUOTENAME(@ConstraintName) + N';';
        EXEC sys.sp_executesql @DropSql;
        FETCH NEXT FROM CheckConstraints INTO @ConstraintName;
    END;
    CLOSE CheckConstraints;
    DEALLOCATE CheckConstraints;

    IF COL_LENGTH(N'rmms.RollLiveState', N'PreviousLengthMm') IS NULL
    BEGIN
        IF COL_LENGTH(N'rmms.RollLiveState', N'LastResetLengthMm') IS NULL
            THROW 51002, N'Neither PreviousLengthMm nor LastResetLengthMm exists.', 1;

        EXEC sys.sp_rename
            N'rmms.RollLiveState.LastResetLengthMm',
            N'PreviousLengthMm',
            N'COLUMN';
    END;

    IF COL_LENGTH(N'rmms.RollLiveState', N'PulseCount') IS NOT NULL
        ALTER TABLE [rmms].[RollLiveState] DROP COLUMN [PulseCount];

    EXEC sys.sp_executesql N'
        ALTER TABLE [rmms].[RollLiveState] WITH CHECK
            ADD CONSTRAINT [CK_rmms_RollLiveState_Roll]
            CHECK ([RollNumber] BETWEEN 1 AND 5);';

    -- Dynamic SQL is required: PreviousLengthMm only exists after sp_rename.
    EXEC sys.sp_executesql N'
        ALTER TABLE [rmms].[RollLiveState] WITH CHECK
            ADD CONSTRAINT [CK_rmms_RollLiveState_Values]
            CHECK
            (
                [CurrentLengthMm] BETWEEN 0 AND 429496729500
                AND [PreviousLengthMm] BETWEEN 0 AND 429496729500
                AND [SpeedMmPerMinute] BETWEEN 0 AND 6553500
            );';

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0
        ROLLBACK TRANSACTION;
    THROW;
END CATCH;
GO

CREATE OR ALTER VIEW [rmms].[vRollLiveState]
AS
    SELECT
        RollNumber,
        IsConfigured,
        IsActive,
        CAST(CurrentLengthMm / 1000.0 AS decimal(19, 1)) AS CurrentLength,
        CAST(PreviousLengthMm / 1000.0 AS decimal(19, 1)) AS PreviousLength,
        CAST(SpeedMmPerMinute / 1000.0 AS decimal(19, 1)) AS SpeedMetersPerMinute,
        SpliceSignal,
        ResetSignal,
        InputError,
        UpdatedAtUtc
    FROM [rmms].[RollLiveState];
GO

CREATE OR ALTER VIEW [rmms].[vCollectorStatus]
AS
    SELECT
        CollectorName,
        PlcOnline,
        PlcQueueCount,
        PlcOverflowCount,
        PlcHeartbeat,
        LastPollAtUtc,
        LastEventAtUtc,
        LastError,
        UpdatedAtUtc,
        CAST(CASE
            WHEN PlcOnline = 1
             AND DATEDIFF(SECOND, UpdatedAtUtc, SYSUTCDATETIME()) BETWEEN 0 AND 10
            THEN 1 ELSE 0
        END AS bit) AS IsOnlineNow
    FROM [rmms].[CollectorStatus];
GO

SELECT
    c.column_id AS ColumnNumber,
    c.name AS ColumnName,
    TYPE_NAME(c.user_type_id) AS DataType
FROM sys.columns AS c
WHERE c.object_id = OBJECT_ID(N'[rmms].[RollLiveState]')
ORDER BY c.column_id;

IF COL_LENGTH(N'rmms.RollLiveState', N'PreviousLengthMm') IS NULL
    THROW 51003, N'Migration failed: PreviousLengthMm was not created.', 1;

IF COL_LENGTH(N'rmms.RollLiveState', N'LastResetLengthMm') IS NOT NULL
    THROW 51004, N'Migration failed: LastResetLengthMm still exists.', 1;

IF COL_LENGTH(N'rmms.RollLiveState', N'PulseCount') IS NOT NULL
    THROW 51005, N'Migration failed: PulseCount still exists.', 1;

IF OBJECT_ID(N'[rmms].[vRollLiveState]', N'V') IS NULL
    THROW 51006, N'Migration failed: vRollLiveState was not created.', 1;

SELECT N'Operational SQL objects are ready.' AS Result;
GO
