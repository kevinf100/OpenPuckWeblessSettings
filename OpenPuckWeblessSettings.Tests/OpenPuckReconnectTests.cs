using OpenPuckWeblessSettings.Usb;

namespace OpenPuckWeblessSettings.Tests;

public sealed class OpenPuckReconnectTests
{
    [Fact]
    public async Task ReconnectsAfterUsbAddressChangesUsingUniqueVidPidFallback()
    {
        var before = new OpenPuckDevice("3:2", 0x28DE, 0x1304, "Steam Controller Puck", "", 0, 0x81, 0x01);
        var after = before with { DeviceKey = "3:7" };
        var transport = new ReenumeratingTransport(after);
        await using var session = new OpenPuckSession(transport);

        await session.ConnectAsync(before, CancellationToken.None);
        var reconnected = await session.ReconnectAfterRebootAsync(TimeSpan.FromSeconds(2), CancellationToken.None);

        Assert.True(reconnected);
        Assert.Equal("3:7", session.Device?.DeviceKey);
        Assert.Equal(2, transport.ConnectCalls);
    }

    private sealed class ReenumeratingTransport(OpenPuckDevice reenumeratedDevice) : IOpenPuckUsbTransport
    {
        public bool IsConnected { get; private set; }
        public int ConnectCalls { get; private set; }

        public Task<IReadOnlyList<OpenPuckDevice>> ScanAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<OpenPuckDevice>>([reenumeratedDevice]);

        public Task ConnectAsync(OpenPuckDevice device, CancellationToken cancellationToken)
        {
            ConnectCalls++;
            IsConnected = true;
            return Task.CompletedTask;
        }

        public Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<byte[]> ReadAsync(int maximumLength, TimeSpan timeout, CancellationToken cancellationToken)
        {
            var payload = new byte[12];
            payload[0] = 17;
            return Task.FromResult(new byte[] { 0xA5, 12 }.Concat(payload).ToArray());
        }

        public Task DisconnectAsync()
        {
            IsConnected = false;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
