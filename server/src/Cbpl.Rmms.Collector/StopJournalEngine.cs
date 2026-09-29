using Cbpl.Rmms.Collector.Configuration;
using Cbpl.Rmms.Collector.Diagnostics;
using Cbpl.Rmms.Collector.Modbus;
using Cbpl.Rmms.Collector.Protocol;

namespace Cbpl.Rmms.Collector;

public enum StopJournalCycleResult
{
    Idle,
    StoredReadOnly,
    Acknowledged
}

public interface IStopJournalSink
{
    Task<bool> StoreAsync(StopJournalEvent item, CancellationToken cancellationToken);
}

public sealed class StopJournalEngine(
    PlcOptions plcOptions,
    IModbusClient modbus,
    IStopJournalSink sink,
    AppLog log)
{
    public async Task<StopJournalCycleResult> CollectOnceAsync(
        CancellationToken cancellationToken)
    {
        ushort[] firstWords = await modbus.ReadHoldingRegistersAsync(
            StopJournalProtocol.BaseAddress,
            StopJournalProtocol.SnapshotWords,
            cancellationToken);
        StopJournalSnapshot first = StopJournalProtocol.ParseSnapshot(firstWords);

        if (first.QueueStatus is 3 or 4)
            throw new ProtocolException(
                $"PLC stop journal reports fatal queue status {first.QueueStatus}.");
        if (first.Event is null)
            return StopJournalCycleResult.Idle;

        // Двойное чтение не позволяет сохранить пакет, изменившийся на границе цикла PLC.
        ushort[] stableWords = await modbus.ReadHoldingRegistersAsync(
            StopJournalProtocol.BaseAddress,
            StopJournalProtocol.SnapshotWords,
            cancellationToken);
        StopJournalSnapshot stable = StopJournalProtocol.ParseSnapshot(stableWords);
        if (stable.Event is null ||
            !firstWords.AsSpan(0, StopJournalProtocol.EventDataWords)
                .SequenceEqual(stableWords.AsSpan(0, StopJournalProtocol.EventDataWords)))
            throw new ProtocolException("PLC stop journal head changed while it was being read.");

        StopJournalEvent item = stable.Event;
        bool inserted = await sink.StoreAsync(item, cancellationToken);
        log.Information(inserted
            ? $"Stored ProductionLine EventId={item.EventId} state={item.StateCode} queue={stable.QueueCount}."
            : $"ProductionLine EventId={item.EventId} was already stored; ACK will be retried.");

        if (!plcOptions.EnableWrites)
            return StopJournalCycleResult.StoredReadOnly;

        await AcknowledgeAsync(item, cancellationToken);
        log.Information($"PLC stop journal acknowledged EventId={item.EventId}.");
        return StopJournalCycleResult.Acknowledged;
    }

    private async Task AcknowledgeAsync(
        StopJournalEvent item,
        CancellationToken cancellationToken)
    {
        await modbus.WriteMultipleRegistersAsync(
            StopJournalProtocol.AckEventIdAddress,
            EventProtocol.SplitUInt32(item.EventId),
            cancellationToken);
        await modbus.WriteSingleRegisterAsync(
            StopJournalProtocol.AckCommandAddress,
            StopJournalProtocol.AckCommand,
            cancellationToken);

        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        TimeSpan timeout = TimeSpan.FromSeconds(plcOptions.AckVerifyTimeoutSeconds);
        while (System.Diagnostics.Stopwatch.GetElapsedTime(started) < timeout)
        {
            ushort[] verification = await modbus.ReadHoldingRegistersAsync(
                StopJournalProtocol.BaseAddress,
                StopJournalProtocol.AckVerificationWords,
                cancellationToken);
            ushort ackCommand = verification[
                StopJournalProtocol.AckCommandAddress - StopJournalProtocol.BaseAddress];
            ushort ackResult = verification[
                StopJournalProtocol.LastAckResultAddress - StopJournalProtocol.BaseAddress];
            ushort ready = verification[StopJournalProtocol.EventReadyIndex];

            bool headChanged = ready == 0;
            if (ready == 1)
            {
                if (verification[0] != StopJournalProtocol.Signature)
                    throw new ProtocolException("Next stop journal head has an invalid signature.");
                uint currentId = EventProtocol.JoinUInt32(verification[2], verification[3]);
                headChanged = currentId != item.EventId;
            }

            if (ackCommand == 0 && ackResult == 2)
                throw new InvalidOperationException(
                    $"PLC rejected stop journal ACK for EventId {item.EventId}.");
            if (ackCommand == 0 && ackResult == 1 && headChanged)
                return;

            await Task.Delay(
                TimeSpan.FromSeconds(Math.Min(0.05, plcOptions.PollIntervalSeconds)),
                cancellationToken);
        }

        throw new TimeoutException(
            $"PLC did not confirm stop journal ACK for EventId {item.EventId}.");
    }
}
