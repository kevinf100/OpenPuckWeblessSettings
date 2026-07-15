namespace OpenPuckWeblessSettings.Usb;

public sealed class OpenPuckConnectionCoordinator(IOpenPuckUsbTransport transport) : IAsyncDisposable
{
    private readonly OpenPuckSession _session = new(transport);
    private bool _disposed;

    public bool IsConnected { get; private set; }

    public Task<IReadOnlyList<OpenPuckDevice>> ScanAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _session.ScanAsync(cancellationToken);
    }

    public async Task<ConnectionProbeResult> ConnectAsync(
        OpenPuckDevice device,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            var profile = await _session.ConnectAsync(device, cancellationToken);
            var result = profile.Kind == OpenPuckTargetKind.ReversePuck
                ? new ConnectionProbeResult(OpenPuckReplyType.ReversePuckStatus, profile.ProtocolVersion,
                    _session.ReversePuckStatus?.RadioLinked == true, (_session.ReversePuckStatus?.RawPayload.Length ?? 0) + 2)
                : new ConnectionProbeResult(OpenPuckReplyType.PuckStatus, profile.ProtocolVersion,
                    _session.PuckStatus?.RadioPeerLinked == true, (_session.PuckStatus?.RawPayload.Length ?? 0) + 2);
            IsConnected = true;
            return result;
        }
        catch
        {
            IsConnected = false;
            throw;
        }
    }

    public async Task DisconnectAsync()
    {
        if (_disposed)
        {
            return;
        }

        await _session.DisconnectAsync();
        IsConnected = false;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await _session.DisposeAsync();
        IsConnected = false;
        _disposed = true;
    }
}
