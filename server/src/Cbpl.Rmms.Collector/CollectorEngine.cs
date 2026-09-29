using Cbpl.Rmms.Collector.Configuration;
using Cbpl.Rmms.Collector.Diagnostics;
using Cbpl.Rmms.Collector.Modbus;
using Cbpl.Rmms.Collector.Protocol;
using Cbpl.Rmms.Collector.Storage;
using System.Diagnostics;

namespace Cbpl.Rmms.Collector;

public enum CycleResult
{
    Idle,
    StoredReadOnly,
    Acknowledged
}

public sealed class CollectorEngine(
    PlcOptions options,
    IModbusClient modbus,
    EventStore store,
    AppLog log)
{
    private ushort _heartbeat;
    private long _lastHeartbeatTimestamp;

    public async Task<CycleResult> CollectOnceAsync(CancellationToken cancellationToken)
    {
        await SendHeartbeatIfDueAsync(cancellationToken);
        ushort[] snapshot = await modbus.ReadHoldingRegistersAsync(
            EventProtocol.EventBaseAddress,
            EventProtocol.SnapshotWords,
            cancellationToken);

        if (snapshot.Length != EventProtocol.SnapshotWords)
            throw new ProtocolException($"Invalid snapshot length {snapshot.Length}.");

        ushort eventReady = snapshot[EventProtocol.EventReadyIndex];
        ushort queueCount = snapshot[EventProtocol.QueueCountIndex];
        ushort queueStatus = snapshot[EventProtocol.QueueStatusIndex];
        await store.SetMetaAsync("queue_count", queueCount.ToString(), cancellationToken);
        await store.SetMetaAsync("queue_status", queueStatus.ToString(), cancellationToken);

        if (eventReady == 0)
            return CycleResult.Idle;
        if (eventReady != 1)
            throw new ProtocolException($"Invalid EventReady value {eventReady}.");
        if (queueCount == 0)
            throw new ProtocolException("EventReady is 1 while QueueCount is 0.");
        if (queueStatus is 3 or 4)
            throw new ProtocolException($"PLC queue reports fatal status {queueStatus}.");

        RollEvent rollEvent = EventProtocol.Parse(snapshot.AsSpan(0, EventProtocol.EventPacketWords));
        bool inserted = await store.SaveEventAsync(rollEvent, cancellationToken);
        if (inserted)
        {
            log.Information(
                $"Stored EventId={rollEvent.EventId} unwind={rollEvent.UnwindNumber} " +
                $"reason={rollEvent.EndReason} length_mm={rollEvent.LengthMm} queue={queueCount}.");
        }
        else
        {
            log.Debug($"EventId={rollEvent.EventId} is already stored.");
        }

        if (!options.EnableWrites)
            return CycleResult.StoredReadOnly;

        await AcknowledgeAsync(rollEvent, cancellationToken);
        await store.MarkPlcAcknowledgedAsync(rollEvent.EventId, cancellationToken);
        log.Information($"PLC acknowledged EventId={rollEvent.EventId}.");
        return CycleResult.Acknowledged;
    }

    private async Task SendHeartbeatIfDueAsync(CancellationToken cancellationToken)
    {
        if (!options.EnableWrites)
            return;

        long now = Stopwatch.GetTimestamp();
        if (_lastHeartbeatTimestamp != 0 &&
            Stopwatch.GetElapsedTime(_lastHeartbeatTimestamp, now) <
            TimeSpan.FromSeconds(options.HeartbeatIntervalSeconds))
            return;

        _heartbeat++;
        await modbus.WriteSingleRegisterAsync(
            EventProtocol.CollectorHeartbeatAddress,
            _heartbeat,
            cancellationToken);
        _lastHeartbeatTimestamp = now;
    }

    private async Task AcknowledgeAsync(RollEvent rollEvent, CancellationToken cancellationToken)
    {
        if (!await store.HasEventAsync(rollEvent.EventId, cancellationToken))
            throw new InvalidOperationException(
                $"Refusing to ACK EventId {rollEvent.EventId}: it is absent from SQLite.");

        await modbus.WriteMultipleRegistersAsync(
            EventProtocol.AckEventIdAddress,
            EventProtocol.SplitUInt32(rollEvent.EventId),
            cancellationToken);

        // Safety invariant: the command is always a separate, final Modbus write.
        await modbus.WriteSingleRegisterAsync(
            EventProtocol.AckCommandAddress,
            EventProtocol.AckCommand,
            cancellationToken);

        long started = Stopwatch.GetTimestamp();
        TimeSpan timeout = TimeSpan.FromSeconds(options.AckVerifyTimeoutSeconds);
        while (Stopwatch.GetElapsedTime(started) < timeout)
        {
            ushort[] verification = await modbus.ReadHoldingRegistersAsync(
                EventProtocol.EventBaseAddress,
                21,
                cancellationToken);
            ushort ackCommand = verification[
                EventProtocol.AckCommandAddress - EventProtocol.EventBaseAddress];
            ushort ackResult = verification[
                EventProtocol.LastAckResultAddress - EventProtocol.EventBaseAddress];
            ushort currentReady = verification[EventProtocol.EventReadyIndex];

            bool headChanged = currentReady == 0;
            if (currentReady == 1)
            {
                if (verification[0] != EventProtocol.Signature)
                    throw new ProtocolException("The next queue head has an invalid signature.");
                uint currentEventId = EventProtocol.JoinUInt32(verification[2], verification[3]);
                headChanged = currentEventId != rollEvent.EventId;
            }

            if (ackCommand == 0 && ackResult == 2)
                throw new InvalidOperationException(
                    $"PLC rejected ACK for EventId {rollEvent.EventId}.");
            if (ackCommand == 0 && ackResult == 1 && headChanged)
                return;

            await Task.Delay(
                TimeSpan.FromSeconds(Math.Min(0.05, options.PollIntervalSeconds)),
                cancellationToken);
        }

        throw new TimeoutException(
            $"PLC did not confirm ACK for EventId {rollEvent.EventId}.");
    }
}

