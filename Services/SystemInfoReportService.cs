using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using LibUsbDotNet;
using LibUsbDotNet.LibUsb;
using OpenPuckWeblessSettings.Usb;

namespace OpenPuckWeblessSettings.Services;

internal sealed record HostSystemInfo(
    string OsDescription,
    string OsVersion,
    Architecture OsArchitecture,
    Architecture ProcessArchitecture,
    string FrameworkDescription,
    string RuntimeVersion,
    int ProcessorCount,
    bool Is64BitOperatingSystem,
    bool Is64BitProcess,
    string Culture,
    string UiCulture,
    string TimeZone,
    string AvaloniaVersion,
    string LibUsbDotNetVersion);

internal interface IHostSystemInfoProvider
{
    HostSystemInfo GetInfo();
}

internal sealed class RuntimeHostSystemInfoProvider : IHostSystemInfoProvider
{
    public HostSystemInfo GetInfo() => new(
        RuntimeInformation.OSDescription,
        Environment.OSVersion.VersionString,
        RuntimeInformation.OSArchitecture,
        RuntimeInformation.ProcessArchitecture,
        RuntimeInformation.FrameworkDescription,
        Environment.Version.ToString(),
        Environment.ProcessorCount,
        Environment.Is64BitOperatingSystem,
        Environment.Is64BitProcess,
        CultureInfo.CurrentCulture.Name,
        CultureInfo.CurrentUICulture.Name,
        TimeZoneInfo.Local.Id,
        VersionOf(typeof(Application).Assembly),
        VersionOf(typeof(UsbContext).Assembly));

    private static string VersionOf(Assembly assembly) =>
        assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? assembly.GetName().Version?.ToString()
        ?? "unknown";
}

internal sealed record UsbInventoryInterface(
    byte Number,
    byte Class,
    byte SubClass,
    IReadOnlyList<UsbEndpointDescriptor> Endpoints);

internal sealed record UsbInventoryDevice(
    ushort VendorId,
    ushort ProductId,
    int BusNumber,
    int Address,
    IReadOnlyList<UsbInventoryInterface> Interfaces,
    string? Error = null);

internal interface IUsbInventoryProvider
{
    Task<IReadOnlyList<UsbInventoryDevice>> ScanAsync(CancellationToken cancellationToken);
}

internal sealed class LibUsbInventoryProvider : IUsbInventoryProvider
{
    public Task<IReadOnlyList<UsbInventoryDevice>> ScanAsync(CancellationToken cancellationToken) =>
        Task.Run(() => Scan(cancellationToken), cancellationToken);

    private static IReadOnlyList<UsbInventoryDevice> Scan(CancellationToken cancellationToken)
    {
        using var context = new UsbContext();
        using var collection = context.List();
        var results = new List<UsbInventoryDevice>();
        foreach (var device in collection.OfType<UsbDevice>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var interfaces = device.Configs
                    .SelectMany(configuration => configuration.Interfaces)
                    .Select(candidate => new UsbInventoryInterface(
                        (byte)candidate.Number,
                        (byte)candidate.Class,
                        (byte)candidate.SubClass,
                        candidate.Endpoints
                            .Select(endpoint => new UsbEndpointDescriptor(
                                (byte)endpoint.EndpointAddress,
                                (byte)endpoint.Attributes))
                            .ToArray()))
                    .ToArray();
                results.Add(new UsbInventoryDevice(
                    (ushort)device.VendorId,
                    (ushort)device.ProductId,
                    device.BusNumber,
                    device.Address,
                    interfaces));
            }
            catch (Exception exception)
            {
                results.Add(new UsbInventoryDevice(
                    (ushort)device.VendorId,
                    (ushort)device.ProductId,
                    device.BusNumber,
                    device.Address,
                    [],
                    exception.Message));
            }
        }

        return results
            .OrderBy(device => device.VendorId)
            .ThenBy(device => device.ProductId)
            .ThenBy(device => device.BusNumber)
            .ThenBy(device => device.Address)
            .ToArray();
    }
}

internal sealed record SystemInfoReportRequest
{
    public required string AppVersion { get; init; }
    public required bool IncludeAllUsbDevices { get; init; }
    public required bool ShowHardwareIdentifiers { get; init; }
    public required bool IsConnected { get; init; }
    public IReadOnlyList<OpenPuckDevice> DiscoveredDevices { get; init; } = [];
    public OpenPuckDevice? SelectedDevice { get; init; }
    public OpenPuckTargetProfile? Profile { get; init; }
    public PuckStatusSnapshot? PuckStatus { get; init; }
    public ReversePuckStatusSnapshot? ReversePuckStatus { get; init; }
    public OpenPuckSettingsDraft? AppliedSettings { get; init; }
    public OpenPuckSettingsDraft? UnsavedSettings { get; init; }
    public IReadOnlyList<string> SerialPorts { get; init; } = [];
    public string? SerialPortError { get; init; }
}

internal sealed class SystemInfoReportService(
    IHostSystemInfoProvider host,
    IUsbInventoryProvider usb,
    Func<DateTimeOffset>? clock = null)
{
    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.Now);

    public async Task<string> GenerateAsync(SystemInfoReportRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var output = new StringBuilder();
        output.AppendLine("# OpenPuck System Info");
        output.AppendLine();
        output.AppendLine($"- Generated: `{_clock():O}`");
        output.AppendLine($"- Hardware identifiers: `{(request.ShowHardwareIdentifiers ? "shown" : "redacted")}`");
        output.AppendLine($"- All USB devices: `{(request.IncludeAllUsbDevices ? "included" : "not requested")}`");

        AppendHost(output, request.AppVersion);
        AppendOpenPuck(output, request);
        AppendSerialPorts(output, request);

        if (request.IncludeAllUsbDevices)
        {
            try
            {
                AppendUsbInventory(output, await usb.ScanAsync(cancellationToken), request.ShowHardwareIdentifiers);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                AppendHeading(output, "All USB devices");
                output.AppendLine($"- Probe error: `{OneLine(exception.Message)}`");
            }
        }

        return output.ToString();
    }

    private void AppendHost(StringBuilder output, string appVersion)
    {
        AppendHeading(output, "Application and host");
        output.AppendLine($"- App version: `{appVersion}`");
        try
        {
            var info = host.GetInfo();
            output.AppendLine($"- OS: `{info.OsDescription}` (`{info.OsVersion}`)");
            output.AppendLine($"- Architecture: OS `{info.OsArchitecture}`, process `{info.ProcessArchitecture}`; OS 64-bit `{info.Is64BitOperatingSystem}`, process 64-bit `{info.Is64BitProcess}`");
            output.AppendLine($"- Runtime: `{info.FrameworkDescription}` (CLR `{info.RuntimeVersion}`)");
            output.AppendLine($"- Avalonia: `{info.AvaloniaVersion}`");
            output.AppendLine($"- LibUsbDotNet: `{info.LibUsbDotNetVersion}`");
            output.AppendLine($"- Logical processors: `{info.ProcessorCount}`");
            output.AppendLine($"- Locale: culture `{info.Culture}`, UI `{info.UiCulture}`, timezone `{info.TimeZone}`");
        }
        catch (Exception exception)
        {
            output.AppendLine($"- Host probe error: `{OneLine(exception.Message)}`");
        }
    }

    private static void AppendOpenPuck(StringBuilder output, SystemInfoReportRequest request)
    {
        AppendHeading(output, "OpenPuck USB and connection");
        output.AppendLine($"- Connection: `{(request.IsConnected ? "connected" : "disconnected")}`");
        output.AppendLine($"- Discovered targets: `{request.DiscoveredDevices.Count}`");
        foreach (var device in request.DiscoveredDevices)
        {
            output.AppendLine($"  - {DeviceLine(device, request.ShowHardwareIdentifiers)}");
        }
        if (request.DiscoveredDevices.Count == 0)
        {
            output.AppendLine("  - None");
        }

        if (request.SelectedDevice is { } selected)
        {
            output.AppendLine($"- Selected target: {DeviceLine(selected, request.ShowHardwareIdentifiers)}");
        }
        else
        {
            output.AppendLine("- Selected target: `none`");
        }

        if (request.Profile is { } profile)
        {
            output.AppendLine($"- Target profile: kind `{profile.Kind}`, protocol `{profile.ProtocolVersion}`, build `{Value(profile.BuildId)}`, dirty `{profile.DirtyBuild}`");
            output.AppendLine($"- Capabilities: settings `{profile.Capabilities.HasSettings}`, per-controller `{profile.Capabilities.HasPerControllerSettings}`, multi-controller status `{profile.Capabilities.HasMultiControllerStatus}`, diagnostics `{profile.Capabilities.HasDiagnostics}`, updater `{profile.Capabilities.HasFirmwareUpdater}`, lizard map `{profile.Capabilities.HasLizardMap}`, backup `{profile.Capabilities.HasBackup}`");
        }
        else
        {
            output.AppendLine("- Target profile: `unavailable`");
        }

        if (request.PuckStatus is { } puck)
        {
            AppendPuckStatus(output, puck, request.ShowHardwareIdentifiers);
        }
        else if (request.ReversePuckStatus is { } reverse)
        {
            AppendReversePuckStatus(output, reverse, request.ShowHardwareIdentifiers);
        }
        else
        {
            AppendHeading(output, "Live target status");
            output.AppendLine("- No live status snapshot is available.");
        }

        AppendSettings(output, "Applied configuration", request.AppliedSettings);
        AppendSettings(output, "Unsaved UI configuration", request.UnsavedSettings);
    }

    private static void AppendPuckStatus(StringBuilder output, PuckStatusSnapshot status, bool showIdentifiers)
    {
        AppendHeading(output, "Puck status and controllers");
        output.AppendLine($"- Firmware: build `{Value(status.BuildId)}`, dirty `{status.DirtyBuild}`, protocol `{status.ProtocolVersion}`");
        output.AppendLine($"- USB/config: mode `{status.Mode}`, persistent mode `{status.PersistMode}`, rumble `{status.RumbleScale}`, switch rate `{status.SwitchProRate}`, gyro scale `{status.SwitchGyroScale}`, land-all-87 `{status.LandAll87}`");
        output.AppendLine($"- RF: linked `{status.RadioPeerLinked}`, battery `{status.BatteryPercent}%`, RSSI `-{status.RssiMagnitude} dBm`");
        output.AppendLine($"- Rates: polls `{status.PollsPerSecond}/s`, delivered `{status.DeliveredPerSecond}/s`, new `{status.NewPerSecond}/s`, relay `{status.RelayFramesPerSecond}/s`");
        output.AppendLine($"- Failures: CRC `{status.CrcFailures}`, no RX `{status.NoRxFailures}`, heal `{status.HealCount}`, ring `{status.RingFaults}`");
        output.AppendLine($"- Timing: loop `{status.LoopMicroseconds} us`, worst stage `{status.WorstStage}` / `{status.WorstStageMicroseconds} us`, poll `{status.PollMicroseconds} us`, stalled stage `{status.LoopStage}` / `{status.LoopStallMilliseconds} ms`");
        output.AppendLine($"- Clocks: LF `{status.LowFrequencyClock}`, HF `{status.HighFrequencyClock}`, `{status.MicrosecondsPerMillisecond} us/ms`");
        output.AppendLine($"- IMU accel: `({status.AccelX}, {status.AccelY}, {status.AccelZ})`");
        output.AppendLine($"- Reset/hang: reason `{status.ResetReason}`, RESETREAS `0x{status.RawResetReason:X8}`, hang stage `{status.HangStage}`, PC `0x{status.HangProgramCounter:X8}`, LR `0x{status.HangLinkRegister:X8}`, USB stack `{status.UsbdFreeStackWords} words`");
        output.AppendLine($"- Controller slots: `{status.Slots.Count}`");
        foreach (var slot in status.Slots)
        {
            output.AppendLine($"  - Slot `{slot.Slot}`: linked `{slot.Linked}`, battery `{slot.BatteryPercent}%`, RSSI `-{slot.RssiMagnitude} dBm`, polls `{slot.PollsPerSecond}/s`, delivered `{slot.DeliveredPerSecond}/s`, new `{slot.NewPerSecond}/s`, failures CRC/no-RX/relay `{slot.CrcFailures}/{slot.NoRxFailures}/{slot.RelayFailures}`");
        }
        if (status.Slots.Count == 0) output.AppendLine("  - None");
        if (showIdentifiers) output.AppendLine($"- Raw status payload: `{Convert.ToHexString(status.RawPayload)}`");
        else output.AppendLine("- Raw status payload: `[redacted]`");
    }

    private static void AppendReversePuckStatus(StringBuilder output, ReversePuckStatusSnapshot status, bool showIdentifiers)
    {
        AppendHeading(output, "ReversePuck status and bonds");
        output.AppendLine($"- Protocol: `{status.ProtocolVersion}`");
        output.AppendLine($"- Forwarding Steam Deck: `{status.ForwardingDeck}`");
        output.AppendLine($"- Radio linked: `{status.RadioLinked}`");
        output.AppendLine($"- Bonds: `{status.Bonds.Count}`");
        foreach (var bond in status.Bonds)
        {
            var serial = showIdentifiers ? Value(bond.Serial) : "[redacted]";
            var puckUuid = showIdentifiers ? Convert.ToHexString(bond.PuckUuid) : "[redacted]";
            var identityUuid = showIdentifiers ? Convert.ToHexString(bond.IdentityUuid) : "[redacted]";
            output.AppendLine($"  - Slot `{bond.Slot}`: alive `{bond.Alive}`, serial `{serial}`, puck UUID `{puckUuid}`, identity UUID `{identityUuid}`");
        }
        if (status.Bonds.Count == 0) output.AppendLine("  - None");
        if (showIdentifiers) output.AppendLine($"- Raw status payload: `{Convert.ToHexString(status.RawPayload)}`");
        else output.AppendLine("- Raw status payload: `[redacted]`");
    }

    private static void AppendSettings(StringBuilder output, string heading, OpenPuckSettingsDraft? settings)
    {
        AppendHeading(output, heading);
        if (settings is null)
        {
            output.AppendLine("- Not available.");
            return;
        }
        output.AppendLine($"- Mode `{settings.Mode}`, mouse divisor `{settings.MouseDivisor}`, mouse friction `{settings.MouseFriction}`, rumble `{settings.RumbleScale}`, persistent mode `{settings.PersistMode}`");
        output.AppendLine($"- Chords: `{string.Join(",", settings.Chords)}`; Switch rate `{settings.SwitchProRate}`; gyro scale `{settings.SwitchGyroScale}`; land-all-87 `{settings.LandAll87}`");
        for (var index = 0; index < settings.PerTypeSettings.Length; index++)
        {
            output.AppendLine($"- Controller type `{index}` settings: `{string.Join(",", settings.PerTypeSettings[index])}`");
        }
    }

    private static void AppendSerialPorts(StringBuilder output, SystemInfoReportRequest request)
    {
        AppendHeading(output, "Serial ports");
        if (!string.IsNullOrWhiteSpace(request.SerialPortError))
        {
            output.AppendLine($"- Probe error: `{OneLine(request.SerialPortError)}`");
            return;
        }
        output.AppendLine($"- Count: `{request.SerialPorts.Count}`");
        if (request.SerialPorts.Count == 0) output.AppendLine("- None");
        else if (!request.ShowHardwareIdentifiers) output.AppendLine($"- Names: `[redacted: {request.SerialPorts.Count} port name(s)]`");
        else foreach (var port in request.SerialPorts) output.AppendLine($"- `{port}`");
    }

    private static void AppendUsbInventory(StringBuilder output, IReadOnlyList<UsbInventoryDevice> devices, bool showIdentifiers)
    {
        AppendHeading(output, "All USB devices");
        output.AppendLine($"- Count: `{devices.Count}`");
        if (devices.Count == 0) output.AppendLine("- None");
        foreach (var device in devices)
        {
            var location = showIdentifiers ? $", bus `{device.BusNumber}`, address `{device.Address}`" : string.Empty;
            output.AppendLine($"- VID:PID `{device.VendorId:X4}:{device.ProductId:X4}`{location}");
            if (!string.IsNullOrWhiteSpace(device.Error)) output.AppendLine($"  - Descriptor error: `{OneLine(device.Error)}`");
            if (device.Interfaces.Count == 0 && string.IsNullOrWhiteSpace(device.Error)) output.AppendLine("  - No interface descriptors available");
            foreach (var usbInterface in device.Interfaces)
            {
                var endpoints = usbInterface.Endpoints.Count == 0
                    ? "none"
                    : string.Join(", ", usbInterface.Endpoints.Select(endpoint => $"0x{endpoint.Address:X2}/attr-0x{endpoint.Attributes:X2}"));
                output.AppendLine($"  - Interface `{usbInterface.Number}` class/subclass `0x{usbInterface.Class:X2}/0x{usbInterface.SubClass:X2}`, endpoints `{endpoints}`");
            }
        }
    }

    private static string DeviceLine(OpenPuckDevice device, bool showIdentifiers)
    {
        var product = string.IsNullOrWhiteSpace(device.Product) ? "OpenPuck target" : device.Product;
        var identity = showIdentifiers
            ? $", serial `{Value(device.SerialNumber)}`, key `{Value(device.DeviceKey)}`"
            : ", serial `[redacted]`, key `[redacted]`";
        return $"{product}, VID:PID `{device.VendorId:X4}:{device.ProductId:X4}`, interface `{device.InterfaceNumber}`, IN `0x{device.BulkInEndpoint:X2}`, OUT `0x{device.BulkOutEndpoint:X2}`{identity}";
    }

    private static void AppendHeading(StringBuilder output, string heading)
    {
        output.AppendLine();
        output.AppendLine($"## {heading}");
        output.AppendLine();
    }

    private static string Value(string? value) => string.IsNullOrWhiteSpace(value) ? "unknown" : OneLine(value);
    private static string OneLine(string value) => value.Replace('\r', ' ').Replace('\n', ' ').Replace('`', '\'');
}
