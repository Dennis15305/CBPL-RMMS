using System.Text.Json;

namespace Cbpl.Rmms.Collector.Configuration;

public sealed class AppConfig
{
    public PlcOptions Plc { get; init; } = new();
    public StorageOptions Storage { get; init; } = new();
    public SqlServerOptions SqlServer { get; init; } = new();
    public HistoryBufferOptions HistoryBuffer { get; init; } = new();
    public StopJournalOptions StopJournal { get; init; } = new();
    public LoggingOptions Logging { get; init; } = new();

    public static AppConfig Load(string configPath)
    {
        string absolutePath = Path.GetFullPath(configPath);
        string json = File.ReadAllText(absolutePath);
        AppConfig config = JsonSerializer.Deserialize<AppConfig>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? throw new InvalidOperationException("Configuration file is empty.");

        string baseDirectory = Path.GetDirectoryName(absolutePath)
            ?? throw new InvalidOperationException("Cannot resolve configuration directory.");
        config.Storage.DatabasePath = ResolvePath(baseDirectory, config.Storage.DatabasePath);
        config.Logging.FilePath = ResolvePath(baseDirectory, config.Logging.FilePath);
        config.Validate();
        return config;
    }

    private static string ResolvePath(string baseDirectory, string value)
    {
        return Path.GetFullPath(Path.IsPathRooted(value) ? value : Path.Combine(baseDirectory, value));
    }

    private void Validate()
    {
        if (string.IsNullOrWhiteSpace(Plc.Host))
            throw new InvalidOperationException("Plc.Host must not be empty.");
        if (Plc.Port is < 1 or > 65535)
            throw new InvalidOperationException("Plc.Port must be between 1 and 65535.");
        if (Plc.UnitId is < 0 or > 255)
            throw new InvalidOperationException("Plc.UnitId must be between 0 and 255.");
        if (Plc.ConnectTimeoutSeconds <= 0 || Plc.PollIntervalSeconds <= 0 ||
            Plc.HeartbeatIntervalSeconds <= 0 || Plc.AckVerifyTimeoutSeconds <= 0)
            throw new InvalidOperationException("PLC timeouts and intervals must be positive.");
        if (Logging.MaxBytes <= 0 || Logging.BackupCount < 0)
            throw new InvalidOperationException("Invalid log rotation settings.");
        if (Storage.DatabaseRole is not ("Test" or "Production"))
            throw new InvalidOperationException(
                "Storage.DatabaseRole must be either 'Test' or 'Production'.");
        if (SqlServer.Enabled)
        {
            if (string.IsNullOrWhiteSpace(SqlServer.ConnectionString))
                throw new InvalidOperationException(
                    "SqlServer.ConnectionString must not be empty.");

            if (SqlServer.Schema is not ("rmms_test" or "rmms"))
                throw new InvalidOperationException(
                    "SqlServer.Schema must be either 'rmms_test' or 'rmms'.");

            if (Storage.DatabaseRole == "Test" && SqlServer.Schema != "rmms_test")
                throw new InvalidOperationException(
                    "The Test database role requires the rmms_test SQL schema.");

            if (Storage.DatabaseRole == "Production" && SqlServer.Schema != "rmms")
                throw new InvalidOperationException(
                    "The Production database role requires the rmms SQL schema.");

            if (string.IsNullOrWhiteSpace(SqlServer.ControllerCode))
                throw new InvalidOperationException(
                    "SqlServer.ControllerCode must not be empty.");

            if (SqlServer.BatchSize is < 1 or > 1000)
                throw new InvalidOperationException(
                    "SqlServer.BatchSize must be between 1 and 1000.");

            if (SqlServer.SyncIntervalSeconds <= 0)
                throw new InvalidOperationException(
                    "SqlServer.SyncIntervalSeconds must be positive.");
        }

        if (Storage.CentralSyncEnabled && !SqlServer.Enabled)
            throw new InvalidOperationException(
                "CentralSyncEnabled requires SqlServer.Enabled.");

        if (HistoryBuffer.Enabled && !SqlServer.Enabled)
            throw new InvalidOperationException(
                "HistoryBuffer.Enabled requires SqlServer.Enabled.");

        if (HistoryBuffer.Enabled && !Plc.EnableWrites)
            throw new InvalidOperationException(
                "HistoryBuffer.Enabled requires Plc.EnableWrites.");

        if (HistoryBuffer.SyncIntervalSeconds <= 0)
            throw new InvalidOperationException(
                "HistoryBuffer.SyncIntervalSeconds must be positive.");

        if (StopJournal.PollIntervalSeconds <= 0)
            throw new InvalidOperationException(
                "StopJournal.PollIntervalSeconds must be positive.");
        if (StopJournal.SourceEpoch <= 0)
            throw new InvalidOperationException(
                "StopJournal.SourceEpoch must be positive.");
        if (string.IsNullOrWhiteSpace(StopJournal.LineName)
            || StopJournal.LineName.Length > 50)
            throw new InvalidOperationException(
                "StopJournal.LineName must contain 1..50 characters.");
        if (StopJournal.Enabled && !SqlServer.Enabled)
            throw new InvalidOperationException(
                "StopJournal.Enabled requires SqlServer.Enabled.");
        if (StopJournal.Enabled && !Plc.EnableWrites)
            throw new InvalidOperationException(
                "StopJournal.Enabled requires Plc.EnableWrites because SQL commits must be acknowledged.");
        if (StopJournal.Enabled && Storage.DatabaseRole != "Production")
            throw new InvalidOperationException(
                "StopJournal can write dbo.RMMS_EventSink only with the Production database role.");

        if (Storage.DatabaseRole == "Test" && Plc.EnableWrites && !Plc.AllowTestWrites)
            throw new InvalidOperationException(
                "PLC writes with the Test database require Plc.AllowTestWrites.");

        if (Storage.DatabaseRole == "Production" && Plc.AllowTestWrites)
            throw new InvalidOperationException(
                "Plc.AllowTestWrites must be false with the Production database.");
    }
}

public sealed class PlcOptions
{
    public string Host { get; init; } = "127.0.0.1";
    public int Port { get; init; } = 502;
    public int UnitId { get; init; } = 1;
    public int RegisterOffset { get; init; }
    public double ConnectTimeoutSeconds { get; init; } = 3.0;
    public double PollIntervalSeconds { get; init; } = 0.5;
    public double HeartbeatIntervalSeconds { get; init; } = 1.0;
    public double AckVerifyTimeoutSeconds { get; init; } = 3.0;
    public bool EnableWrites { get; init; }
    public bool AllowTestWrites { get; init; }
}

public sealed class StorageOptions
{
    public string DatabasePath { get; set; } = "data/cbpl_rmms.db";
    public string DatabaseRole { get; init; } = "Production";
    public bool CentralSyncEnabled { get; init; }
}
public sealed class SqlServerOptions
{
    public bool Enabled { get; init; }

    public string ConnectionString { get; init; } =
        "Server=localhost;Database=RMMS_Demo;" +
        "Integrated Security=True;TrustServerCertificate=True;";

    public string Schema { get; init; } = "rmms";
    public string ControllerCode { get; init; } = "TM241_205";
    public int BatchSize { get; init; } = 20;
    public double SyncIntervalSeconds { get; init; } = 2.0;
}

public sealed class HistoryBufferOptions
{
    public bool Enabled { get; init; }
    public double SyncIntervalSeconds { get; init; } = 2.0;
}

public sealed class StopJournalOptions
{
    public bool Enabled { get; init; }
    public string LineName { get; init; } = "ProductionLine";
    // Увеличить вручную только после преднамеренной очистки persistent-памяти PLC.
    public int SourceEpoch { get; init; } = 1;
    public double PollIntervalSeconds { get; init; } = 0.5;
}

public sealed class LoggingOptions
{
    public string Level { get; init; } = "Information";
    public string FilePath { get; set; } = "logs/collector.log";
    public long MaxBytes { get; init; } = 5 * 1024 * 1024;
    public int BackupCount { get; init; } = 5;
}
