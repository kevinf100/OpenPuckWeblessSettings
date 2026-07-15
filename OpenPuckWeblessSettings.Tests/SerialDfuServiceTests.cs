using OpenPuckWeblessSettings.Services;

namespace OpenPuckWeblessSettings.Tests;

public sealed class SerialDfuServiceTests
{
    [Fact]
    public void EnumeratesDistinctSortedPorts()
    {
        var adapter = new RecordingAdapter(["COM9", "COM2", "com2", ""]);
        var service = new SerialDfuService(adapter);
        Assert.Equal(["COM2", "COM9"], service.GetPortNames());
    }

    [Fact]
    public async Task SendsOne1200BaudTouchToSelectedPort()
    {
        var adapter = new RecordingAdapter(["COM7"]);
        var service = new SerialDfuService(adapter);
        await service.EnterDfuAsync("COM7", CancellationToken.None);
        Assert.Equal(["COM7"], adapter.TouchedPorts);
    }

    [Fact]
    public async Task RejectsMissingPortWithoutTouchingHardware()
    {
        var adapter = new RecordingAdapter(["COM7"]);
        var service = new SerialDfuService(adapter);
        await Assert.ThrowsAsync<IOException>(() => service.EnterDfuAsync("COM8", CancellationToken.None));
        Assert.Empty(adapter.TouchedPorts);
    }

    [Fact]
    public async Task HonorsCancellationBeforeTouchingHardware()
    {
        var adapter = new RecordingAdapter(["COM7"]);
        var service = new SerialDfuService(adapter);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.EnterDfuAsync("COM7", cancellation.Token));
        Assert.Empty(adapter.TouchedPorts);
    }

    [Fact]
    public async Task SurfacesPortAccessFailures()
    {
        var adapter = new RecordingAdapter(["COM7"]) { Failure = new UnauthorizedAccessException("busy") };
        var service = new SerialDfuService(adapter);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.EnterDfuAsync("COM7", CancellationToken.None));
    }

    private sealed class RecordingAdapter(IReadOnlyList<string> ports) : ISerialPortAdapter
    {
        public List<string> TouchedPorts { get; } = [];
        public Exception? Failure { get; init; }
        public IReadOnlyList<string> GetPortNames() => ports;
        public void Touch1200Baud(string portName)
        {
            if (Failure is not null) throw Failure;
            TouchedPorts.Add(portName);
        }
    }
}
