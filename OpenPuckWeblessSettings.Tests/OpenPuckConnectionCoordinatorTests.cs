using OpenPuckWeblessSettings.Usb;

namespace OpenPuckWeblessSettings.Tests;

public sealed class OpenPuckConnectionCoordinatorTests
{
    private static readonly OpenPuckDevice Device =
        new("1:2", 0x28DE, 0x1142, "OpenPuck", "test", 4, 0x84, 0x04);

    [Fact]
    public async Task SuccessfulProbeKeepsConnectionUntilDisconnect()
    {
        var fake = new FakeTransport
        {
            Reads = new Queue<byte[]>([BuildStatus(true)])
        };
        await using var coordinator = new OpenPuckConnectionCoordinator(fake);

        var result = await coordinator.ConnectAsync(Device, CancellationToken.None);

        Assert.True(result.RadioPeerLinked);
        Assert.True(coordinator.IsConnected);
        Assert.Equal(0, fake.DisconnectCalls);

        await coordinator.DisconnectAsync();
        Assert.False(coordinator.IsConnected);
        Assert.Equal(1, fake.DisconnectCalls);
    }

    [Theory]
    [InlineData("timeout")]
    [InlineData("claim")]
    public async Task FailedProbeAlwaysRequestsCleanup(string failure)
    {
        var fake = new FakeTransport
        {
            ConnectFailure = failure == "claim" ? new OpenPuckUsbException("claim failed") : null,
            ReadFailure = failure == "timeout" ? new TimeoutException("read timed out") : null
        };
        await using var coordinator = new OpenPuckConnectionCoordinator(fake);

        await Assert.ThrowsAnyAsync<Exception>(() => coordinator.ConnectAsync(Device, CancellationToken.None));

        Assert.False(coordinator.IsConnected);
        Assert.Equal(1, fake.DisconnectCalls);
    }

    [Fact]
    public async Task CancellationPropagatesAndRequestsCleanup()
    {
        var fake = new FakeTransport
        {
            ConnectDelay = Timeout.InfiniteTimeSpan
        };
        await using var coordinator = new OpenPuckConnectionCoordinator(fake);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(25));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            coordinator.ConnectAsync(Device, cancellation.Token));

        Assert.Equal(1, fake.DisconnectCalls);
        Assert.False(coordinator.IsConnected);
    }

    private sealed class FakeTransport : IOpenPuckUsbTransport
    {
        public Queue<byte[]> Reads { get; init; } = [];
        public Exception? ConnectFailure { get; init; }
        public Exception? ReadFailure { get; init; }
        public TimeSpan ConnectDelay { get; init; }
        public int DisconnectCalls { get; private set; }
        public bool IsConnected { get; private set; }

        public Task<IReadOnlyList<OpenPuckDevice>> ScanAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<OpenPuckDevice>>([Device]);

        public async Task ConnectAsync(OpenPuckDevice device, CancellationToken cancellationToken)
        {
            if (ConnectDelay != default) await Task.Delay(ConnectDelay, cancellationToken);
            if (ConnectFailure is not null) throw ConnectFailure;
            IsConnected = true;
        }

        public Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<byte[]> ReadAsync(int maximumLength, TimeSpan timeout, CancellationToken cancellationToken)
        {
            if (ReadFailure is not null) return Task.FromException<byte[]>(ReadFailure);
            return Task.FromResult(Reads.Count > 0 ? Reads.Dequeue() : BuildStatus(true));
        }

        public Task DisconnectAsync()
        {
            DisconnectCalls++;
            IsConnected = false;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static byte[] BuildStatus(bool linked)
    {
        var result = new byte[14];
        result[0] = 0xA5;
        result[1] = 12;
        result[2] = 16;
        result[13] = linked ? (byte)1 : (byte)0;
        return result;
    }
}
