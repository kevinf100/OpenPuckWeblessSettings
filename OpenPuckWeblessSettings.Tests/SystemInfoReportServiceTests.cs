using System.Runtime.InteropServices;
using OpenPuckWeblessSettings.Services;
using OpenPuckWeblessSettings.Usb;

namespace OpenPuckWeblessSettings.Tests;

public sealed class SystemInfoReportServiceTests
{
    private static readonly DateTimeOffset GeneratedAt = new(2026, 7, 16, 12, 34, 56, TimeSpan.FromHours(-4));

    [Fact]
    public async Task DisconnectedReportIsDeterministicAndDescribesEmptyCollections()
    {
        var usb = new FakeUsbInventoryProvider([]);
        var report = await Service(usb).GenerateAsync(new SystemInfoReportRequest
        {
            AppVersion = "1.2.3",
            IncludeAllUsbDevices = false,
            ShowHardwareIdentifiers = false,
            IsConnected = false
        }, CancellationToken.None);

        Assert.Contains("2026-07-16T12:34:56.0000000-04:00", report);
        Assert.Contains("App version: `1.2.3`", report);
        Assert.Contains("Connection: `disconnected`", report);
        Assert.Contains("Discovered targets: `0`", report);
        Assert.Contains("No live status snapshot is available", report);
        Assert.Contains("## Serial ports", report);
        Assert.Contains("Count: `0`", report);
        Assert.DoesNotContain("## All USB devices", report);
        Assert.Equal(0, usb.Calls);
    }

    [Fact]
    public async Task PuckReportIncludesTelemetryControllersAndConfigurationsButRedactsIdentifiers()
    {
        var device = Device();
        var report = await Service().GenerateAsync(new SystemInfoReportRequest
        {
            AppVersion = "1.2.3",
            IncludeAllUsbDevices = false,
            ShowHardwareIdentifiers = false,
            IsConnected = true,
            DiscoveredDevices = [device],
            SelectedDevice = device,
            Profile = new OpenPuckTargetProfile(OpenPuckTargetKind.Puck, 17, "build-17", true, OpenPuckCapabilities.For(OpenPuckTargetKind.Puck, 17)),
            PuckStatus = PuckStatus(),
            AppliedSettings = Settings(4),
            UnsavedSettings = Settings(8),
            SerialPorts = ["COM-SECRET"]
        }, CancellationToken.None);

        Assert.Contains("## Puck status and controllers", report);
        Assert.Contains("Slot `0`: linked `True`", report);
        Assert.Contains("RESETREAS `0x12345678`", report);
        Assert.Contains("## Applied configuration", report);
        Assert.Contains("Mode `4`", report);
        Assert.Contains("## Unsaved UI configuration", report);
        Assert.Contains("Mode `8`", report);
        Assert.Contains("Raw status payload: `[redacted]`", report);
        Assert.Contains("[redacted: 1 port name(s)]", report);
        Assert.DoesNotContain("SERIAL-SECRET", report);
        Assert.DoesNotContain("DEVICE-KEY-SECRET", report);
        Assert.DoesNotContain("COM-SECRET", report);
        Assert.DoesNotContain("AABBCC", report);
    }

    [Fact]
    public async Task IdentifierOptionIncludesKnownIdentifiersAndRawPayload()
    {
        var device = Device();
        var report = await Service().GenerateAsync(new SystemInfoReportRequest
        {
            AppVersion = "1.2.3",
            IncludeAllUsbDevices = false,
            ShowHardwareIdentifiers = true,
            IsConnected = true,
            DiscoveredDevices = [device],
            SelectedDevice = device,
            PuckStatus = PuckStatus(),
            SerialPorts = ["COM-SECRET"]
        }, CancellationToken.None);

        Assert.Contains("SERIAL-SECRET", report);
        Assert.Contains("DEVICE-KEY-SECRET", report);
        Assert.Contains("COM-SECRET", report);
        Assert.Contains("Raw status payload: `AABBCC`", report);
    }

    [Fact]
    public async Task ReversePuckReportRedactsAndRevealsBondIdentifiersAccordingToOption()
    {
        var reverse = new ReversePuckStatusSnapshot(17, true, true,
            [new ReversePuckBond(2, true, [0xAA, 0xBB], [0xCC, 0xDD], "BOND-SECRET")],
            [0xAC, 0x01]);
        var redacted = await Service().GenerateAsync(BaseRequest() with
        {
            IsConnected = true,
            ReversePuckStatus = reverse
        }, CancellationToken.None);
        var shown = await Service().GenerateAsync(BaseRequest() with
        {
            IsConnected = true,
            ShowHardwareIdentifiers = true,
            ReversePuckStatus = reverse
        }, CancellationToken.None);

        Assert.Contains("## ReversePuck status and bonds", redacted);
        Assert.DoesNotContain("BOND-SECRET", redacted);
        Assert.DoesNotContain("AABB", redacted);
        Assert.Contains("BOND-SECRET", shown);
        Assert.Contains("puck UUID `AABB`", shown);
        Assert.Contains("Raw status payload: `AC01`", shown);
    }

    [Fact]
    public async Task AllUsbInventoryIsOptionalAndRedactsLocationsByDefault()
    {
        var inventory = new UsbInventoryDevice(0x1234, 0x5678, 7, 9,
            [new UsbInventoryInterface(1, 0x03, 0x01, [new UsbEndpointDescriptor(0x81, 0x03)])]);
        var usb = new FakeUsbInventoryProvider([inventory]);
        var hidden = await Service(usb).GenerateAsync(BaseRequest() with
        {
            IncludeAllUsbDevices = true
        }, CancellationToken.None);
        var shown = await Service(usb).GenerateAsync(BaseRequest() with
        {
            IncludeAllUsbDevices = true,
            ShowHardwareIdentifiers = true
        }, CancellationToken.None);

        Assert.Contains("VID:PID `1234:5678`", hidden);
        Assert.Contains("class/subclass `0x03/0x01`", hidden);
        Assert.DoesNotContain("bus `7`", hidden);
        Assert.Contains("bus `7`, address `9`", shown);
        Assert.Equal(2, usb.Calls);
    }

    [Fact]
    public async Task ProbeFailuresProducePartialReports()
    {
        var service = new SystemInfoReportService(
            new ThrowingHostProvider(),
            new ThrowingUsbProvider(),
            () => GeneratedAt);
        var report = await service.GenerateAsync(BaseRequest() with
        {
            IncludeAllUsbDevices = true,
            SerialPortError = "serial failed"
        }, CancellationToken.None);

        Assert.Contains("App version: `1.2.3`", report);
        Assert.Contains("Host probe error: `host failed`", report);
        Assert.Contains("Probe error: `serial failed`", report);
        Assert.Contains("Probe error: `usb failed`", report);
    }

    private static SystemInfoReportService Service(IUsbInventoryProvider? usb = null) => new(
        new FakeHostProvider(),
        usb ?? new FakeUsbInventoryProvider([]),
        () => GeneratedAt);

    private static SystemInfoReportRequest BaseRequest() => new()
    {
        AppVersion = "1.2.3",
        IncludeAllUsbDevices = false,
        ShowHardwareIdentifiers = false,
        IsConnected = false
    };

    private static OpenPuckDevice Device() => new(
        "DEVICE-KEY-SECRET", 0x28DE, 0x1304, "OpenPuck", "SERIAL-SECRET", 2, 0x81, 0x02);

    private static OpenPuckSettingsDraft Settings(byte mode) => new()
    {
        Mode = mode,
        MouseDivisor = 20,
        MouseFriction = 3,
        RumbleScale = 100,
        PersistMode = true,
        Chords = [1, 2, 3],
        SwitchProRate = 2,
        SwitchGyroScale = 10,
        PerTypeSettings = [[1, 2], [3, 4]],
        LandAll87 = false
    };

    private static PuckStatusSnapshot PuckStatus() => new(
        ProtocolVersion: 17,
        Mode: 4,
        MouseDivisor: 20,
        MouseFriction: 3,
        RadioPeerLinked: true,
        DeliveredPerSecond: 498,
        NewPerSecond: 200,
        PersistMode: true,
        Chords: [1, 2, 3],
        PollsPerSecond: 500,
        LoopMicroseconds: 100,
        WorstStage: 2,
        WorstStageMicroseconds: 40,
        PollMicroseconds: 2000,
        BatteryPercent: 80,
        RssiMagnitude: 45,
        DirtyBuild: true,
        BuildId: "build-17",
        RumbleScale: 100,
        SwitchProRate: 2,
        SwitchGyroScale: 10,
        AccelX: 1,
        AccelY: 2,
        AccelZ: 3,
        Slots: [new ControllerSlotStatus(0, true, 75, 42, 500, 498, 200, 1, 2, 3)],
        PerTypeSettings: [[1, 2], [3, 4]],
        ResetReason: 3,
        RawResetReason: 0x12345678,
        HangStage: 2,
        HangProgramCounter: 0x10203040,
        HangLinkRegister: 0x50607080,
        UsbdFreeStackWords: 321,
        RelayFramesPerSecond: 250,
        LowFrequencyClock: 1,
        HighFrequencyClock: 2,
        MicrosecondsPerMillisecond: 1000,
        LoopStage: 1,
        LoopStallMilliseconds: 0,
        CrcFailures: 1,
        NoRxFailures: 2,
        HealCount: 3,
        RingFaults: 4,
        LandAll87: false,
        RawPayload: [0xAA, 0xBB, 0xCC]);

    private sealed class FakeHostProvider : IHostSystemInfoProvider
    {
        public HostSystemInfo GetInfo() => new(
            "Test OS", "Test OS 1", Architecture.X64, Architecture.X64,
            ".NET Test", "10.0.0", 8, true, true,
            "en-US", "en-US", "Test/Zone", "12.1.0", "3.0.224");
    }

    private sealed class ThrowingHostProvider : IHostSystemInfoProvider
    {
        public HostSystemInfo GetInfo() => throw new InvalidOperationException("host failed");
    }

    private sealed class FakeUsbInventoryProvider(IReadOnlyList<UsbInventoryDevice> devices) : IUsbInventoryProvider
    {
        public int Calls { get; private set; }

        public Task<IReadOnlyList<UsbInventoryDevice>> ScanAsync(CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(devices);
        }
    }

    private sealed class ThrowingUsbProvider : IUsbInventoryProvider
    {
        public Task<IReadOnlyList<UsbInventoryDevice>> ScanAsync(CancellationToken cancellationToken) =>
            Task.FromException<IReadOnlyList<UsbInventoryDevice>>(new IOException("usb failed"));
    }
}
