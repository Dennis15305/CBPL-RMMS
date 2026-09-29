using Cbpl.Rmms.Collector.Configuration;
using System.Buffers.Binary;
using System.Net.Sockets;

namespace Cbpl.Rmms.Collector.Modbus;

public sealed class ModbusTcpClient(PlcOptions options) : IModbusClient
{
    private int _transactionId;

    public async Task<ushort[]> ReadHoldingRegistersAsync(
        ushort address,
        ushort count,
        CancellationToken cancellationToken)
    {
        if (count is < 1 or > 125)
            throw new ArgumentOutOfRangeException(nameof(count));

        byte[] payload = new byte[4];
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(0, 2), WireAddress(address));
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(2, 2), count);
        byte[] response = await RequestAsync(3, payload, cancellationToken);

        if (response.Length != 1 + count * 2 || response[0] != count * 2)
            throw new ModbusException("Unexpected read-holding-registers response.");

        ushort[] values = new ushort[count];
        for (int index = 0; index < count; index++)
            values[index] = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(1 + index * 2, 2));
        return values;
    }

    public async Task WriteSingleRegisterAsync(
        ushort address,
        ushort value,
        CancellationToken cancellationToken)
    {
        byte[] payload = new byte[4];
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(0, 2), WireAddress(address));
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(2, 2), value);
        byte[] response = await RequestAsync(6, payload, cancellationToken);
        if (!response.AsSpan().SequenceEqual(payload))
            throw new ModbusException("Write-single-register echo does not match the request.");
    }

    public async Task WriteMultipleRegistersAsync(
        ushort address,
        IReadOnlyList<ushort> values,
        CancellationToken cancellationToken)
    {
        if (values.Count is < 1 or > 123)
            throw new ArgumentOutOfRangeException(nameof(values));

        byte[] payload = new byte[5 + values.Count * 2];
        ushort wireAddress = WireAddress(address);
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(0, 2), wireAddress);
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(2, 2), (ushort)values.Count);
        payload[4] = checked((byte)(values.Count * 2));
        for (int index = 0; index < values.Count; index++)
            BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(5 + index * 2, 2), values[index]);

        byte[] response = await RequestAsync(16, payload, cancellationToken);
        if (response.Length != 4 ||
            BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(0, 2)) != wireAddress ||
            BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(2, 2)) != values.Count)
            throw new ModbusException("Write-multiple-registers response does not match the request.");
    }

    private ushort WireAddress(ushort documentedAddress)
    {
        int address = documentedAddress + options.RegisterOffset;
        if (address is < 0 or > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(documentedAddress));
        return (ushort)address;
    }

    private async Task<byte[]> RequestAsync(
        byte function,
        byte[] payload,
        CancellationToken cancellationToken)
    {
        ushort transactionId = (ushort)Interlocked.Increment(ref _transactionId);
        byte[] request = new byte[8 + payload.Length];
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(0, 2), transactionId);
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(2, 2), 0);
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(4, 2), checked((ushort)(payload.Length + 2)));
        request[6] = checked((byte)options.UnitId);
        request[7] = function;
        payload.CopyTo(request, 8);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.ConnectTimeoutSeconds));

        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(options.Host, options.Port, timeout.Token);
            using NetworkStream stream = client.GetStream();
            await stream.WriteAsync(request, timeout.Token);

            byte[] header = new byte[7];
            await ReadExactlyAsync(stream, header, timeout.Token);
            ushort responseTransaction = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(0, 2));
            ushort protocolId = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(2, 2));
            ushort length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4, 2));
            if (responseTransaction != transactionId || protocolId != 0 ||
                header[6] != options.UnitId || length < 2)
                throw new ModbusException("Invalid Modbus TCP header.");

            byte[] pdu = new byte[length - 1];
            await ReadExactlyAsync(stream, pdu, timeout.Token);
            if (pdu[0] == (function | 0x80))
                throw new ModbusException($"PLC returned Modbus exception {pdu.ElementAtOrDefault(1)}.");
            if (pdu[0] != function)
                throw new ModbusException($"Unexpected response function 0x{pdu[0]:X2}.");
            return pdu[1..];
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ModbusException(
                $"Communication with {options.Host}:{options.Port} timed out.", exception);
        }
        catch (Exception exception) when (exception is SocketException or IOException)
        {
            throw new ModbusException(
                $"Cannot communicate with {options.Host}:{options.Port}: {exception.Message}", exception);
        }
    }

    private static async Task ReadExactlyAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer[offset..], cancellationToken);
            if (read == 0)
                throw new IOException("Connection closed before the complete Modbus response was received.");
            offset += read;
        }
    }
}

public sealed class ModbusException : IOException
{
    public ModbusException(string message) : base(message) { }
    public ModbusException(string message, Exception innerException) : base(message, innerException) { }
}
