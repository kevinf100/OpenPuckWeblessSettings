namespace OpenPuckWeblessSettings.Usb;

public sealed class OpenPuckSession(IOpenPuckUsbTransport transport) : IAsyncDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(600);
    private readonly SemaphoreSlim _operationQueue = new(1, 1);
    private CancellationTokenSource? _sessionCancellation;
    private Task? _pollTask;
    private bool _disposed;

    public OpenPuckDevice? Device { get; private set; }
    public OpenPuckTargetProfile? Profile { get; private set; }
    public PuckStatusSnapshot? PuckStatus { get; private set; }
    public ReversePuckStatusSnapshot? ReversePuckStatus { get; private set; }
    public bool IsConnected => transport.IsConnected && Device is not null;

    public event EventHandler<PuckStatusSnapshot>? PuckStatusChanged;
    public event EventHandler<ReversePuckStatusSnapshot>? ReversePuckStatusChanged;
    public event EventHandler<OpenPuckFrame>? FrameReceived;
    public event EventHandler<Exception>? ConnectionLost;

    public Task<IReadOnlyList<OpenPuckDevice>> ScanAsync(CancellationToken cancellationToken) =>
        transport.ScanAsync(cancellationToken);

    public async Task<OpenPuckTargetProfile> ConnectAsync(OpenPuckDevice device, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Device is not null || transport.IsConnected)
        {
            await DisconnectAsync();
        }
        try
        {
            await transport.ConnectAsync(device, cancellationToken);
            Device = device;
            _sessionCancellation = new CancellationTokenSource();
            Exception? last = null;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    var frame = await ExchangeAsync([OpenPuckProtocol.GetStatusCommand],
                        device.ProductId == 0x1302 ? (byte)0xAC : (byte)0xA5, 3,
                        TimeSpan.FromSeconds(2), cancellationToken);
                    ApplyFrame(frame);
                    Profile = frame.Marker == 0xAC
                        ? new OpenPuckTargetProfile(OpenPuckTargetKind.ReversePuck, frame.Payload[0], "ReversePuck", false,
                            OpenPuckCapabilities.For(OpenPuckTargetKind.ReversePuck, frame.Payload[0]))
                        : new OpenPuckTargetProfile(OpenPuckTargetKind.Puck, frame.Payload[0], PuckStatus!.BuildId,
                            PuckStatus.DirtyBuild, OpenPuckCapabilities.For(OpenPuckTargetKind.Puck, frame.Payload[0]));
                    _pollTask = PollAsync(_sessionCancellation.Token);
                    return Profile;
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    last = exception;
                    if (attempt < 2) await Task.Delay(PollInterval, cancellationToken);
                }
            }
            throw new OpenPuckUsbException("OpenPuck did not return a valid A5/AC status frame after three attempts.", last);
        }
        catch
        {
            await DisconnectAsync();
            throw;
        }
    }

    public async Task<OpenPuckFrame> ExchangeAsync(
        byte[] command,
        byte replyMarker,
        int minimumPayloadLength,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        return await ExclusiveAsync(async token =>
        {
            await transport.WriteAsync(command, token);
            return await ReadFrameCoreAsync(replyMarker, minimumPayloadLength, timeout, 256, token);
        }, cancellationToken);
    }

    public async Task<T> ExclusiveAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _operationQueue.WaitAsync(cancellationToken);
        try
        {
            if (!transport.IsConnected) throw new OpenPuckUsbException("No OpenPuck target is connected.");
            return await operation(cancellationToken);
        }
        finally
        {
            _operationQueue.Release();
        }
    }

    public Task ExclusiveAsync(Func<CancellationToken, Task> operation, CancellationToken cancellationToken) =>
        ExclusiveAsync(async token => { await operation(token); return true; }, cancellationToken);

    public Task WriteRawAsync(byte[] command, CancellationToken cancellationToken) =>
        transport.WriteAsync(command, cancellationToken);

    public Task<byte[]> ReadRawAsync(int maximumLength, TimeSpan timeout, CancellationToken cancellationToken) =>
        transport.ReadAsync(maximumLength, timeout, cancellationToken);

    public async Task<OpenPuckFrame> ReadFrameCoreAsync(
        byte marker,
        int minimumPayloadLength,
        TimeSpan timeout,
        int maximumLength,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        Exception? lastParseFailure = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var remaining = deadline - DateTimeOffset.UtcNow;
            byte[] bytes;
            try
            {
                bytes = await transport.ReadAsync(maximumLength, remaining, cancellationToken);
            }
            catch (TimeoutException)
            {
                break;
            }
            try
            {
                var frames = OpenPuckFrameCodec.Parse(bytes);
                foreach (var frame in frames)
                {
                    FrameReceived?.Invoke(this, frame);
                    if (frame.Marker == 0xA9 && frame.Payload.Length >= 3) continue;
                    if (frame.Marker == marker && frame.Payload.Length >= minimumPayloadLength) return frame;
                }
            }
            catch (InvalidDataException exception)
            {
                lastParseFailure = exception;
            }
        }
        throw new TimeoutException($"Timed out waiting for a complete 0x{marker:X2} reply.", lastParseFailure);
    }

    public async Task<bool> ReconnectAfterRebootAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var previous = Device ?? throw new InvalidOperationException("No previous OpenPuck target is available.");
        await DisconnectAsync();
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var devices = await transport.ScanAsync(cancellationToken);
                var match = devices.FirstOrDefault(candidate =>
                    (!string.IsNullOrEmpty(previous.SerialNumber) && candidate.SerialNumber == previous.SerialNumber) ||
                    candidate.DeviceKey == previous.DeviceKey);
                match ??= devices.Count(candidate => candidate.VendorId == previous.VendorId && candidate.ProductId == previous.ProductId) == 1
                    ? devices.Single(candidate => candidate.VendorId == previous.VendorId && candidate.ProductId == previous.ProductId)
                    : null;
                if (match is not null)
                {
                    await ConnectAsync(match, cancellationToken);
                    return true;
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Re-enumeration can make libusb enumeration transiently fail.
                _ = exception;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(400), cancellationToken);
        }
        return false;
    }

    public async Task DisconnectAsync()
    {
        if (_sessionCancellation is not null)
        {
            await _sessionCancellation.CancelAsync();
            if (_pollTask is not null && Task.CurrentId != _pollTask.Id)
            {
                try { await _pollTask; } catch (OperationCanceledException) { }
            }
            _sessionCancellation.Dispose();
        }
        _sessionCancellation = null;
        _pollTask = null;
        await transport.DisconnectAsync();
        Device = null;
        Profile = null;
        PuckStatus = null;
        ReversePuckStatus = null;
    }

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var marker = Profile?.Kind == OpenPuckTargetKind.ReversePuck ? (byte)0xAC : (byte)0xA5;
                var frame = await ExchangeAsync([0x01], marker, 3, TimeSpan.FromSeconds(2), cancellationToken);
                ApplyFrame(frame);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                ConnectionLost?.Invoke(this, exception);
                break;
            }
            await Task.Delay(PollInterval, cancellationToken);
        }
    }

    private void ApplyFrame(OpenPuckFrame frame)
    {
        if (frame.Marker == 0xA5)
        {
            PuckStatus = OpenPuckStatusParser.ParsePuck(frame.Payload);
            PuckStatusChanged?.Invoke(this, PuckStatus);
        }
        else if (frame.Marker == 0xAC)
        {
            ReversePuckStatus = OpenPuckStatusParser.ParseReversePuck(frame.Payload);
            ReversePuckStatusChanged?.Invoke(this, ReversePuckStatus);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        await DisconnectAsync();
        await transport.DisposeAsync();
        _operationQueue.Dispose();
        _disposed = true;
    }
}
