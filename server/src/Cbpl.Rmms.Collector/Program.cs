using Cbpl.Rmms.Collector;
using Cbpl.Rmms.Collector.Configuration;
using Cbpl.Rmms.Collector.Diagnostics;
using Cbpl.Rmms.Collector.Modbus;
using Cbpl.Rmms.Collector.Storage;

return await RunAsync(args);

static async Task<int> RunAsync(string[] args)
{
    string defaultConfig = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
    string configPath = GetOption(args, "--config") ?? defaultConfig;
    bool runOnce = args.Contains("--once", StringComparer.OrdinalIgnoreCase);
    bool checkConfig = args.Contains("--check-config", StringComparer.OrdinalIgnoreCase);
    bool initializeDatabase = args.Contains(
        "--initialize-db", StringComparer.OrdinalIgnoreCase);
    bool checkSql = args.Contains("--check-sql", StringComparer.OrdinalIgnoreCase);
    bool checkStopJournal = args.Contains(
        "--check-stop-journal", StringComparer.OrdinalIgnoreCase);
    bool syncOnce = args.Contains("--sync-once", StringComparer.OrdinalIgnoreCase);
    bool syncOnly = args.Contains("--sync-only", StringComparer.OrdinalIgnoreCase);

    AppConfig config;
    try
    {
        config = AppConfig.Load(configPath);
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine($"Configuration error: {exception.Message}");
        return 2;
    }

    if (checkConfig)
    {
        Console.WriteLine($"Configuration is valid: {Path.GetFullPath(configPath)}");
        Console.WriteLine($"PLC: {config.Plc.Host}:{config.Plc.Port}, unit {config.Plc.UnitId}");
        Console.WriteLine($"Database: {config.Storage.DatabasePath}");
        Console.WriteLine($"Database role: {config.Storage.DatabaseRole}");
        Console.WriteLine($"Central sync enabled: {config.Storage.CentralSyncEnabled}");
        Console.WriteLine($"PLC writes enabled: {config.Plc.EnableWrites}");
        Console.WriteLine($"Haiwell history buffer enabled: {config.HistoryBuffer.Enabled}");
        Console.WriteLine($"ProductionLine stop journal enabled: {config.StopJournal.Enabled}");
        Console.WriteLine($"ProductionLine source epoch: {config.StopJournal.SourceEpoch}");
        return 0;
    }

    if (checkStopJournal)
    {
        try
        {
            var sink = new SqlServerStopJournalSink(
                config.SqlServer, config.StopJournal);
            await sink.CheckTargetAsync(CancellationToken.None);
            Console.WriteLine(
                "Stop journal SQL target and duplicate-protection table are available.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Stop journal SQL check failed: {exception.Message}");
            return 1;
        }
    }

    if (checkSql)
    {
        try
        {
            await SqlServerEventSync.CheckConnectionAsync(
                config.SqlServer, CancellationToken.None);
            Console.WriteLine("SQL connection and test controller are available.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"SQL check failed: {exception.Message}");
            return 1;
        }
    }

    var log = new AppLog(config.Logging);
    string schemaPath = Path.Combine(AppContext.BaseDirectory, "schema.sql");
    var store = new EventStore(config.Storage.DatabasePath, schemaPath);
    await store.InitializeAsync(CancellationToken.None);
    await store.SetMetaAsync(
        "database_role", config.Storage.DatabaseRole, CancellationToken.None);
    await store.SetMetaAsync(
        "central_sync_enabled",
        config.Storage.CentralSyncEnabled ? "true" : "false",
        CancellationToken.None);

    if (initializeDatabase)
    {
        log.Information(
            $"Database initialized: {config.Storage.DatabasePath}; " +
            $"role={config.Storage.DatabaseRole}; central_sync={config.Storage.CentralSyncEnabled}.");
        return 0;
    }

    if (syncOnce)
    {
        if (!config.SqlServer.Enabled || !config.Storage.CentralSyncEnabled)
        {
            Console.Error.WriteLine("SQL synchronization is disabled.");
            return 2;
        }

        try
        {
            var sync = new SqlServerEventSync(config.SqlServer, store);
            int count = await sync.SyncOnceAsync(CancellationToken.None);
            log.Information($"SQL sync completed: {count} event(s).");
            return 0;
        }
        catch (Exception exception)
        {
            log.Error("SQL sync failed; local events remain pending.", exception);
            return 1;
        }
    }

    if (syncOnly && (!config.SqlServer.Enabled || !config.Storage.CentralSyncEnabled))
    {
        Console.Error.WriteLine("SQL synchronization is disabled.");
        return 2;
    }

    if (runOnce && syncOnly)
    {
        Console.Error.WriteLine("--once and --sync-only cannot be combined.");
        return 2;
    }

    if (runOnce)
    {
        var oneModbus = new ModbusTcpClient(config.Plc);
        var oneCollector = new CollectorEngine(config.Plc, oneModbus, store, log);
        try
        {
            CycleResult result =
                await oneCollector.CollectOnceAsync(CancellationToken.None);
            log.Information($"Single cycle completed: {result}.");
            return 0;
        }
        catch (Exception exception)
        {
            log.Error("Single cycle failed.", exception);
            return 1;
        }
    }

    using var stop = new CancellationTokenSource();
    Console.CancelKeyPress += (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        stop.Cancel();
    };

    Task sqlTask = config.Storage.CentralSyncEnabled
        ? RunSqlLoopAsync(
            new SqlServerEventSync(config.SqlServer, store),
            config.SqlServer, log, stop.Token)
        : Task.CompletedTask;

    if (syncOnly)
    {
        log.Information("SQL-only mode started; PLC will not be contacted.");
        await sqlTask;
        log.Information("SQL-only mode stopped.");
        return 0;
    }

    var modbus = new ModbusTcpClient(config.Plc);
    var collector = new CollectorEngine(config.Plc, modbus, store, log);
    Task operationalStateTask = config.Storage.CentralSyncEnabled
        ? RunOperationalStateLoopAsync(
            new SqlServerOperationalStateSync(
                config.SqlServer, new ModbusTcpClient(config.Plc)),
            config.SqlServer, log, stop.Token)
        : Task.CompletedTask;
    Task historyBufferTask = config.HistoryBuffer.Enabled
        ? RunHistoryBufferLoopAsync(
            new SqlServerHistoryBufferSync(
                config.SqlServer, new ModbusTcpClient(config.Plc)),
            config.HistoryBuffer, log, stop.Token)
        : Task.CompletedTask;
    Task stopJournalTask = config.StopJournal.Enabled
        ? RunStopJournalLoopAsync(
            new StopJournalEngine(
                config.Plc,
                new ModbusTcpClient(config.Plc),
                new SqlServerStopJournalSink(config.SqlServer, config.StopJournal),
                log),
            config.StopJournal, log, stop.Token)
        : Task.CompletedTask;
    log.Information(config.Plc.EnableWrites
        ? "Collector started in write/ACK mode."
        : "Collector started in read-only mode.");

    try
    {
        while (!stop.IsCancellationRequested)
        {
            long started = Environment.TickCount64;
            try
            {
                await collector.CollectOnceAsync(stop.Token);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                log.Error("Collector cycle failed; retrying.", exception);
            }

            double elapsed = (Environment.TickCount64 - started) / 1000.0;
            double delay = Math.Max(0, config.Plc.PollIntervalSeconds - elapsed);
            await Task.Delay(TimeSpan.FromSeconds(delay), stop.Token);
        }
    }
    catch (OperationCanceledException) when (stop.IsCancellationRequested)
    {
        // Штатная остановка.
    }
    finally
    {
        stop.Cancel();
        await Task.WhenAll(
            sqlTask, operationalStateTask, historyBufferTask, stopJournalTask);
    }

    log.Information("Collector stopped.");
    return 0;
}

static string? GetOption(string[] args, string name)
{
    for (int index = 0; index < args.Length; index++)
    {
        if (!args[index].Equals(name, StringComparison.OrdinalIgnoreCase))
            continue;
        if (index + 1 >= args.Length)
            throw new ArgumentException($"Missing value after {name}.");
        return args[index + 1];
    }
    return null;
}
static async Task RunSqlLoopAsync(
    SqlServerEventSync sync,
    SqlServerOptions options,
    AppLog log,
    CancellationToken cancellationToken)
{
    int failures = 0;

    while (!cancellationToken.IsCancellationRequested)
    {
        double delay = options.SyncIntervalSeconds;

        try
        {
            int count = await sync.SyncOnceAsync(cancellationToken);
            if (count > 0)
                log.Information($"SQL synchronized {count} event(s).");

            failures = 0;
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            break;
        }
        catch (Exception exception)
        {
            failures = Math.Min(failures + 1, 6);
            delay = Math.Min(
                60, options.SyncIntervalSeconds * Math.Pow(2, failures));
            log.Error(
                $"SQL unavailable; retry in {delay:0}s. Events remain in SQLite.",
                exception);
        }

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(delay), cancellationToken);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            break;
        }
    }
}

static async Task RunOperationalStateLoopAsync(
    SqlServerOperationalStateSync sync,
    SqlServerOptions options,
    AppLog log,
    CancellationToken cancellationToken)
{
    int sqlFailures = 0;
    string? lastPlcError = null;

    while (!cancellationToken.IsCancellationRequested)
    {
        double delay = options.SyncIntervalSeconds;

        try
        {
            string? plcError = await sync.SyncOnceAsync(cancellationToken);
            if (plcError is not null
                && !string.Equals(plcError, lastPlcError, StringComparison.Ordinal))
                log.Warning($"PLC operational state unavailable: {plcError}");
            else if (plcError is null && lastPlcError is not null)
                log.Information("PLC operational state reporting recovered.");

            lastPlcError = plcError;
            sqlFailures = 0;
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            break;
        }
        catch (Exception exception)
        {
            sqlFailures = Math.Min(sqlFailures + 1, 6);
            delay = Math.Min(
                60, options.SyncIntervalSeconds * Math.Pow(2, sqlFailures));
            log.Error(
                $"Operational state could not be written to SQL; retry in {delay:0}s.",
                exception);
        }

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(delay), cancellationToken);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            break;
        }
    }
}

static async Task RunHistoryBufferLoopAsync(
    SqlServerHistoryBufferSync sync,
    HistoryBufferOptions options,
    AppLog log,
    CancellationToken cancellationToken)
{
    int failures = 0;

    while (!cancellationToken.IsCancellationRequested)
    {
        double delay = options.SyncIntervalSeconds;

        try
        {
            await sync.SyncOnceAsync(cancellationToken);
            failures = 0;
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            break;
        }
        catch (Exception exception)
        {
            failures = Math.Min(failures + 1, 6);
            delay = Math.Min(
                60, options.SyncIntervalSeconds * Math.Pow(2, failures));
            log.Error(
                $"Haiwell history buffer update failed; retry in {delay:0}s.",
                exception);
        }

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(delay), cancellationToken);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            break;
        }
    }
}

static async Task RunStopJournalLoopAsync(
    StopJournalEngine engine,
    StopJournalOptions options,
    AppLog log,
    CancellationToken cancellationToken)
{
    int failures = 0;

    while (!cancellationToken.IsCancellationRequested)
    {
        double delay = options.PollIntervalSeconds;
        try
        {
            await engine.CollectOnceAsync(cancellationToken);
            failures = 0;
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            break;
        }
        catch (Exception exception)
        {
            failures = Math.Min(failures + 1, 6);
            delay = Math.Min(60, options.PollIntervalSeconds * Math.Pow(2, failures));
            log.Error(
                $"ProductionLine stop journal failed; no PLC ACK was sent. Retry in {delay:0.0}s.",
                exception);
        }

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(delay), cancellationToken);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            break;
        }
    }
}
