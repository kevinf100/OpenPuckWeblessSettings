namespace OpenPuckWeblessSettings.Usb;

public interface IOpenPuckUsbTransport : IAsyncDisposable
{
    Task<IReadOnlyList<OpenPuckDevice>> ScanAsync(CancellationToken cancellationToken);

    bool IsConnected { get; }

    Task ConnectAsync(OpenPuckDevice device, CancellationToken cancellationToken);

    Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken);

    Task<byte[]> ReadAsync(
        int maximumLength,
        TimeSpan timeout,
        CancellationToken cancellationToken);

    Task DisconnectAsync();
}
