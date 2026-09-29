namespace Cbpl.Rmms.Collector.Modbus;

public interface IModbusClient
{
    Task<ushort[]> ReadHoldingRegistersAsync(ushort address, ushort count, CancellationToken cancellationToken);
    Task WriteSingleRegisterAsync(ushort address, ushort value, CancellationToken cancellationToken);
    Task WriteMultipleRegistersAsync(ushort address, IReadOnlyList<ushort> values, CancellationToken cancellationToken);
}

