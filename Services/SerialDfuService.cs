using System.IO.Ports;

namespace OpenPuckWeblessSettings.Services;

public interface ISerialPortAdapter
{
    IReadOnlyList<string> GetPortNames();
    void Touch1200Baud(string portName);
}

public sealed class SystemSerialPortAdapter : ISerialPortAdapter
{
    public IReadOnlyList<string> GetPortNames() => SerialPort.GetPortNames();

    public void Touch1200Baud(string portName)
    {
        using var port = new SerialPort(portName, 1200)
        {
            DtrEnable = false,
            RtsEnable = false,
            ReadTimeout = 1000,
            WriteTimeout = 1000
        };
        port.Open();
        port.DtrEnable = false;
    }
}

public interface ISerialDfuService
{
    IReadOnlyList<string> GetPortNames();
    Task EnterDfuAsync(string portName, CancellationToken cancellationToken);
}

public sealed class SerialDfuService(ISerialPortAdapter? adapter = null) : ISerialDfuService
{
    private readonly ISerialPortAdapter _adapter = adapter ?? new SystemSerialPortAdapter();

    public IReadOnlyList<string> GetPortNames() => _adapter.GetPortNames()
        .Where(port => !string.IsNullOrWhiteSpace(port))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Order(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public async Task EnterDfuAsync(string portName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(portName);
        cancellationToken.ThrowIfCancellationRequested();
        if (!GetPortNames().Contains(portName, StringComparer.OrdinalIgnoreCase))
            throw new IOException($"Serial port {portName} is no longer available. Refresh the port list and try again.");
        await Task.Run(() => _adapter.Touch1200Baud(portName), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
    }
}
