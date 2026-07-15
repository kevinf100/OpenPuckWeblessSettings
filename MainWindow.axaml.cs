using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using OpenPuckWeblessSettings.Services;
using OpenPuckWeblessSettings.Usb;

namespace OpenPuckWeblessSettings;

public sealed partial class MainWindow : Window
{
    private static readonly string[] ModeNames =
    [
        "Steam (puck)", "Xbox 360", "Switch (HORIPAD)", "Lizard (always)", "Switch Pro + gyro",
        "PS5 DualSense", "HID gyro (DS4)", "PS5 game/clean", "DS4 game/clean", "PS3"
    ];
    private static readonly string[] ControllerTypes = ["Xbox", "Switch", "DS4", "DS5"];
    private static readonly string[] MappingNames =
        ["Back L4", "Back R4", "Back L5", "Back R5", "QAM", "A/B + X/Y swap", "Trackpad haptics", "LED brightness", "Rumble"];
    private static readonly string[] MappingHelp =
    [
        "Emulated button code sent when the upper-left back paddle is pressed. Use 0 for no output.",
        "Emulated button code sent when the upper-right back paddle is pressed. Use 0 for no output.",
        "Emulated button code sent when the lower-left back paddle is pressed. Use 0 for no output.",
        "Emulated button code sent when the lower-right back paddle is pressed. Use 0 for no output.",
        "Emulated button code used for the QAM (three-dots) button. Use 0 for the mode default.",
        "Use 1 to swap A with B and X with Y for this emulated controller type; use 0 for the normal layout.",
        "Use 1 to enable trackpad haptics for this emulated controller type, or 0 to disable them.",
        "LED brightness percentage for this emulated controller type. Use 0 for automatic or 1–100 for a fixed level.",
        "Use 1 to enable rumble for this emulated controller type, or 0 to disable it. Overall strength is set above."
    ];
    private static readonly string[] ResetReasons =
        ["unknown", "power-on", "pin/replug", "watchdog (hang)", "CPU lockup", "HARDFAULT", "reboot", "soft reset", "wake-from-off"];
    private static readonly string[] StageNames =
        ["webusb", "ctrl.task", "serial", "rfdiag", "rflink", "haptic", "led", "usbmount", "usbtx"];

    private readonly OpenPuckSession _session;
    private readonly OpenPuckClient _client;
    private readonly OpenPuckBackupService _backup;
    private readonly FirmwareUpdateService _firmware;
    private readonly GitHubFirmwareReleaseService _releases = new();
    private readonly IAppReleaseService _appReleases;
    private readonly IApplicationVersionProvider _appVersion;
    private readonly IExternalUriLauncher _uriLauncher;
    private readonly ISerialDfuService _serialDfu;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<LizardBinding> _lizardBindings = [];
    private readonly List<HangRecord> _hangRecords = [];
    private readonly NumericUpDown[] _typeFields = new NumericUpDown[9];
    private OpenPuckSettingsDraft? _baseline;
    private OpenPuckSettingsDraft? _draft;
    private FirmwareImage? _selectedFirmware;
    private AppRelease? _latestAppRelease;
    private bool _settingsDirty;
    private bool _lizardDirty;
    private bool _busy;
    private bool _advanced;
    private bool _updatingControls;
    private bool _lizardLoaded;
    private bool _updatingDeviceSelection;
    private bool _checkingAppUpdate;
    private bool _serialDfuBusy;
    private bool _stabilityTestActive;
    private bool _closeConfirmationPending;
    private bool _allowClose;
    private int _renderedControllerType = -1;

    public MainWindow() : this(new LibUsbOpenPuckTransport()) { }

    public MainWindow(IOpenPuckUsbTransport transport) : this(
        transport,
        new GitHubAppReleaseService(),
        new AssemblyApplicationVersionProvider(),
        new SystemExternalUriLauncher(),
        new SerialDfuService()) { }

    public MainWindow(
        IOpenPuckUsbTransport transport,
        IAppReleaseService appReleases,
        IApplicationVersionProvider appVersion,
        IExternalUriLauncher uriLauncher,
        ISerialDfuService serialDfu)
    {
        _session = new OpenPuckSession(transport);
        _client = new OpenPuckClient(_session);
        _backup = new OpenPuckBackupService(_client);
        _firmware = new FirmwareUpdateService(_session);
        _appReleases = appReleases;
        _appVersion = appVersion;
        _uriLauncher = uriLauncher;
        _serialDfu = serialDfu;
        InitializeComponent();
        AppVersionText.Text = $"Version {_appVersion.DisplayVersion}";
        BuildModeButtons();
        BuildControllerTypeFields();
        PopulateChoiceLists();
        WireEvents();
        SetConnectedUi(false);
    }

    private void WireEvents()
    {
        Opened += async (_, _) =>
        {
            RefreshSerialPorts();
            await ScanAsync();
            _ = CheckAppUpdatesAsync(false);
        };
        Closed += async (_, _) =>
        {
            await _lifetime.CancelAsync();
            await _session.DisposeAsync();
            _releases.Dispose();
            if (_appReleases is IDisposable disposable) disposable.Dispose();
            _lifetime.Dispose();
        };
        Closing += OnClosing;
        _session.PuckStatusChanged += (_, status) => Dispatcher.UIThread.Post(() => ApplyPuckStatus(status));
        _session.ReversePuckStatusChanged += (_, status) => Dispatcher.UIThread.Post(() => ApplyReversePuckStatus(status));
        _session.FrameReceived += (_, frame) =>
        {
            if (frame.Marker == 0xA9 && frame.Payload.Length >= 3)
            {
                var ms = frame.Payload[1] | frame.Payload[2] << 8;
                Dispatcher.UIThread.Post(() => AppendDiagnostic($"LIVE WEDGE: {Stage(frame.Payload[0])}, {ms} ms"));
            }
        };
        _session.ConnectionLost += (_, exception) => Dispatcher.UIThread.Post(() =>
        {
            SetConnectedUi(false);
            ConnectionState.Text = "Disconnected: " + FlattenException(exception);
        });

        RescanButton.Click += async (_, _) => await ScanAsync();
        ConnectButton.Click += async (_, _) => await ToggleConnectionAsync();
        DevicePicker.SelectionChanged += async (_, _) =>
        {
            UpdateDeviceDetails();
            if (_updatingDeviceSelection) return;
            if (_session.IsConnected && DevicePicker.SelectedItem is OpenPuckDevice selected && selected.DeviceKey != _session.Device?.DeviceKey)
            {
                if (await DisconnectAsync())
                    await ConnectAsync(selected);
            }
        };
        AdvancedButton.Click += async (_, _) => await ToggleAdvancedAsync();
        ControllerTypePicker.SelectionChanged += (_, _) => { SaveCurrentTypeFields(); RenderControllerType(); };
        ControllerApply.Click += async (_, _) => await ApplyControllerSettingsAsync();
        ControllerRevert.Click += (_, _) => RevertControllerSettings();

        foreach (var field in new[] { MouseDivisor, MouseFriction, RumbleScale })
            field.ValueChanged += (_, _) => MarkSettingsDirty();
        foreach (var field in _typeFields)
            field.ValueChanged += (_, _) => MarkSettingsDirty();
        foreach (var picker in new[] { Chord1, Chord2, Chord3, SwitchRate, SwitchGyro })
            picker.SelectionChanged += (_, _) => MarkSettingsDirty();
        PersistMode.IsCheckedChanged += (_, _) => MarkSettingsDirty();
        LandAll87.IsCheckedChanged += (_, _) => MarkSettingsDirty();

        LizardAdd.Click += (_, _) => { _lizardBindings.Add(new LizardBinding { OutputType = 1 }); RenderLizardMap(); SetLizardDirty(true); };
        LizardReload.Click += async (_, _) => await LoadLizardAsync();
        LizardSave.Click += async (_, _) => await SaveLizardAsync();
        LizardReset.Click += async (_, _) =>
        {
            if (await ConfirmAsync("Reset desktop map", "Reset the desktop map to firmware defaults? Unsaved edits are lost."))
                await RunBusyAsync(async token => { ReplaceLizard(await _client.ResetLizardMapAsync(token)); SetLizardDirty(false); LizardStatus.Text = "Reset to device defaults."; });
        };

        BackupExport.Click += async (_, _) => await ExportBackupAsync();
        BackupImport.Click += async (_, _) => await ImportBackupAsync();
        HapticReset.Click += async (_, _) => await RunCommandAsync("Haptic engine reinitialized.", _client.ClearHapticsAsync);
        ControllerOff.Click += async (_, _) => await RunCommandAsync("Controller power-off requested.", _client.PowerOffControllerAsync);
        StabilityTest.Click += async (_, _) => await ToggleStabilityTestAsync();
        CaptureStart.Click += async (_, _) => await RunCommandAsync("Capture started.", _client.StartCaptureAsync);
        CaptureStop.Click += async (_, _) => await StopCaptureAsync();
        CaptureSave.Click += async (_, _) => await SaveTextAsync("puck-capture.txt", CaptureOutput.Text ?? "", "Text capture", "txt");
        FlightLoad.Click += async (_, _) => await LoadFlightAsync();
        HangCsv.Click += async (_, _) => await SaveHangCsvAsync();
        DebugCdc.Click += async (_, _) => await ConfirmedRebootAsync("Debug CDC", "Reboot once with the 115200-baud CDC console enabled? The configuration interface will disconnect.", _client.RebootDebugCdcAsync);
        FactoryErase.Click += async (_, _) => await DestructiveAsync(false);
        WipeBoard.Click += async (_, _) => await DestructiveAsync(true);

        FirmwarePick.Click += async (_, _) => await PickFirmwareAsync();
        FirmwareFlash.Click += async (_, _) => await FlashFirmwareAsync();
        FirmwareAbort.Click += async (_, _) => await RunCommandAsync("Firmware update abort sent.", _firmware.AbortAsync);
        FirmwareReleases.Click += async (_, _) => await LoadReleasesAsync();
        ReleaseStandard.Click += async (_, _) => await SelectReleaseAsync(false);
        ReleaseFactory.Click += async (_, _) => await SelectReleaseAsync(true);
        IncludePrereleases.IsCheckedChanged += async (_, _) => await LoadReleasesAsync();
        ReleaseList.DoubleTapped += async (_, _) => await SelectReleaseAsync(false);
        Uf2Dfu.Click += async (_, _) => await ConfirmedRebootAsync("UF2 DFU", "Reboot into the UF2 bootloader without flashing an image? The target disconnects and should mount as a drive.", _client.RebootUf2DfuAsync);
        SerialDfu.Click += async (_, _) => await ConfirmedRebootAsync("Serial DFU", "Reboot the connected OpenPuck into serial DFU without flashing an image? The target disconnects immediately.", _client.RebootSerialDfuAsync);
        SerialPortRefresh.Click += (_, _) => RefreshSerialPorts();
        SerialPortDfu.Click += async (_, _) => await EnterStandaloneDfuAsync();
        CheckAppUpdates.Click += async (_, _) => await CheckAppUpdatesAsync(true);
        ViewAppRelease.Click += (_, _) => OpenUri(_latestAppRelease?.ReleaseUri ?? GitHubAppReleaseService.ReleasesUri);
        AppUpdateButton.Click += (_, _) => OpenUri(_latestAppRelease?.ReleaseUri ?? GitHubAppReleaseService.ReleasesUri);
        OpenThisRepository.Click += (_, _) => OpenUri(GitHubAppReleaseService.RepositoryUri);
        OpenUpstreamRepository.Click += (_, _) => OpenUri(GitHubAppReleaseService.UpstreamRepositoryUri);
    }

    private async Task CheckAppUpdatesAsync(bool manual)
    {
        if (_checkingAppUpdate) return;
        _checkingAppUpdate = true;
        CheckAppUpdates.IsEnabled = false;
        AppUpdateStatus.Text = "Checking GitHub releases…";
        try
        {
            var result = await _appReleases.CheckAsync(_appVersion.Version, _appVersion.IsPrerelease, _lifetime.Token);
            _latestAppRelease = result.Latest;
            ViewAppRelease.IsEnabled = result.Latest is not null;
            if (result.Latest is null)
            {
                AppUpdateButton.IsVisible = false;
                AppUpdateStatus.Text = "No published app releases are currently available.";
            }
            else if (result.IsUpdateAvailable)
            {
                AppUpdateButton.Content = $"Update v{result.Latest.Version} available";
                AppUpdateButton.IsVisible = true;
                AppUpdateStatus.Text = $"Version {result.Latest.Version} is available: {result.Latest.Name}";
            }
            else
            {
                AppUpdateButton.IsVisible = false;
                AppUpdateStatus.Text = $"Version {_appVersion.DisplayVersion} is current. Latest published version: {result.Latest.Version}.";
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception)
        {
            AppUpdateStatus.Text = "Update check unavailable: " + FlattenException(exception);
            if (manual) AppUpdateStatus.Text += " You can still open this project's GitHub from the links below.";
        }
        finally
        {
            _checkingAppUpdate = false;
            CheckAppUpdates.IsEnabled = true;
        }
    }

    private void RefreshSerialPorts(string? status = null)
    {
        try
        {
            var selected = SerialPortPicker.SelectedItem as string;
            var ports = _serialDfu.GetPortNames();
            SerialPortPicker.ItemsSource = ports;
            SerialPortPicker.SelectedItem = selected is not null && ports.Contains(selected, StringComparer.OrdinalIgnoreCase) ? selected : ports.FirstOrDefault();
            SerialPortDfu.IsEnabled = ports.Count > 0 && !_serialDfuBusy;
            SerialDfuStatus.Text = status ?? (ports.Count == 0
                ? "No serial ports found. Double-tap Reset or briefly short RST to GND twice to enter DFU manually."
                : $"Found {ports.Count} serial port{(ports.Count == 1 ? "" : "s")}. Select the board's port before continuing.");
        }
        catch (Exception exception)
        {
            SerialPortPicker.ItemsSource = Array.Empty<string>();
            SerialPortDfu.IsEnabled = false;
            SerialDfuStatus.Text = "Could not enumerate serial ports: " + FlattenException(exception);
        }
    }

    private async Task EnterStandaloneDfuAsync()
    {
        if (_serialDfuBusy) return;
        if (SerialPortPicker.SelectedItem is not string portName)
        {
            SerialDfuStatus.Text = "Select a serial port first. If none appears, use the board's double-reset method.";
            return;
        }
        if (!await ConfirmAsync("Enter serial bootloader", $"Send a 1200-baud touch to {portName}? This does not flash firmware. A compatible board should disconnect and reappear in its bootloader.")) return;
        _serialDfuBusy = true;
        SerialPortRefresh.IsEnabled = false;
        SerialPortDfu.IsEnabled = false;
        SerialDfuStatus.Text = $"Sending 1200-baud touch to {portName}…";
        try
        {
            await _serialDfu.EnterDfuAsync(portName, _lifetime.Token);
            await Task.Delay(TimeSpan.FromSeconds(1.5), _lifetime.Token);
            RefreshSerialPorts($"1200-baud touch sent to {portName}. The board should re-enumerate in DFU; if it did not, double-tap Reset or briefly short RST to GND twice.");
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (UnauthorizedAccessException exception) { SerialDfuStatus.Text = $"Cannot open {portName}; it may be in use or access may be denied. {FlattenException(exception)}"; }
        catch (Exception exception) { SerialDfuStatus.Text = $"Could not enter DFU through {portName}: {FlattenException(exception)} Use the board's double-reset method instead."; }
        finally
        {
            _serialDfuBusy = false;
            SerialPortRefresh.IsEnabled = true;
            SerialPortDfu.IsEnabled = SerialPortPicker.SelectedItem is string;
        }
    }

    private void OpenUri(Uri uri)
    {
        try { _uriLauncher.Open(uri); }
        catch (Exception exception) { AppUpdateStatus.Text = "Could not open the system browser: " + FlattenException(exception); }
    }

    private async Task ScanAsync()
    {
        if (_busy) return;
        await RunBusyAsync(async token =>
        {
            ConnectionState.Text = "Scanning…";
            var devices = await _session.ScanAsync(token);
            DevicePicker.ItemsSource = devices;
            DevicePicker.SelectedIndex = devices.Count > 0 ? 0 : -1;
            ConnectionState.Text = devices.Count == 0 ? "No OpenPuck target found" : $"{devices.Count} target{(devices.Count == 1 ? "" : "s")} found";
            UpdateDeviceDetails();
        });
    }

    private async Task ToggleConnectionAsync()
    {
        if (_busy) return;
        if (_session.IsConnected) { await DisconnectAsync(); return; }
        if (DevicePicker.SelectedItem is OpenPuckDevice device) await ConnectAsync(device);
    }

    private async Task<bool> ConnectAsync(OpenPuckDevice device)
    {
        if (HasUnsavedChanges && !await ConfirmDiscardChangesAsync("connect and reload settings from the puck"))
            return false;

        await RunBusyAsync(async token =>
        {
            ConnectionState.Text = "Connecting…";
            var profile = await _session.ConnectAsync(device, token);
            ConnectionState.Text = $"Connected · {profile.Kind} · protocol v{profile.ProtocolVersion}";
            ClearUnsavedChanges();
            SetConnectedUi(true);
            if (profile.Capabilities.HasSettings && _session.PuckStatus is { } status)
                ApplyPuckStatus(status);
            if (profile.Capabilities.HasLizardMap) await LoadLizardAsync();
        });
        return _session.IsConnected;
    }

    private async Task<bool> DisconnectAsync()
    {
        if (HasUnsavedChanges && !await ConfirmDiscardChangesAsync("disconnect"))
            return false;

        await RunBusyAsync(async _ => await _session.DisconnectAsync());
        ClearUnsavedChanges();
        SetConnectedUi(false);
        ConnectionState.Text = "Disconnected";
        return true;
    }

    private void ApplyPuckStatus(PuckStatusSnapshot status)
    {
        var profile = _session.Profile;
        OverviewTarget.Text = _session.Device?.DisplayName ?? "OpenPuck puck";
        OverviewBuild.Text = string.IsNullOrWhiteSpace(status.BuildId) ? "unknown" : status.BuildId + (status.DirtyBuild ? " (dirty)" : "");
        OverviewProtocol.Text = $"A5 · v{status.ProtocolVersion}";
        OverviewRf.Text = status.RadioPeerLinked ? "RF peer linked" : "No RF peer linked";
        OverviewRf.Foreground = status.RadioPeerLinked ? Brushes.MediumSeaGreen : Brushes.Goldenrod;
        OverviewMode.Text = status.Mode < ModeNames.Length ? ModeNames[status.Mode] : $"Mode {status.Mode}";
        OverviewUpdater.Text = status.ProtocolVersion >= 15 ? "Panel update supported" : "Manual UF2 only";
        FirmwareGate.Text = status.ProtocolVersion >= 15 ? "Panel firmware updating is available." : "This puck predates panel updates (protocol v15 required). Use UF2 DFU once.";
        FirmwareFlash.IsEnabled = status.ProtocolVersion >= 15 && _selectedFirmware is not null && !_busy;

        SlotStatusList.Children.Clear();
        if (status.Slots.Count == 0)
            SlotStatusList.Children.Add(new TextBlock { Text = status.RadioPeerLinked ? "Controller linked" : "No linked controller" });
        foreach (var slot in status.Slots)
            SlotStatusList.Children.Add(new TextBlock
            {
                Text = $"Controller {slot.Slot + 1}: {(slot.Linked ? "linked" : "offline")} · battery {slot.BatteryPercent}% · RSSI -{slot.RssiMagnitude} dBm · poll {slot.PollsPerSecond}/s · delivered {slot.DeliveredPerSecond}/s ({slot.NewPerSecond} new) · fails CRC {slot.CrcFailures} / no RX {slot.NoRxFailures} / relay {slot.RelayFailures}",
                TextWrapping = TextWrapping.Wrap
            });
        RateStatus.Text = $"poll {status.PollsPerSecond}/s · delivered {status.DeliveredPerSecond}/s · new {status.NewPerSecond}/s · relay {status.RelayFramesPerSecond}/s";
        FailureStatus.Text = $"CRC {status.CrcFailures} · no RX {status.NoRxFailures} · heal {status.HealCount} · ring {status.RingFaults}";
        ClockStatus.Text = $"LF {ClockName(status.LowFrequencyClock, true)} · HF {ClockName(status.HighFrequencyClock, false)} · {status.MicrosecondsPerMillisecond} µs/ms (ideal 1000) · RF poll period {status.PollMicroseconds} µs";
        LoopStatus.Text = status.LoopStallMilliseconds >= 200
            ? $"STALLED @ {Stage(status.LoopStage)} for {status.LoopStallMilliseconds} ms"
            : $"running · {status.LoopMicroseconds} µs · worst {Stage(status.WorstStage)} {status.WorstStageMicroseconds} µs · stack {status.UsbdFreeStackWords} words";
        ImuStatus.Text = $"a=({status.AccelX}, {status.AccelY}, {status.AccelZ}) |a|={Math.Round(Math.Sqrt((long)status.AccelX * status.AccelX + (long)status.AccelY * status.AccelY + (long)status.AccelZ * status.AccelZ))}";
        var reason = status.ResetReason < ResetReasons.Length ? ResetReasons[status.ResetReason] : $"code {status.ResetReason}";
        ResetStatus.Text = $"{reason} · RESETREAS 0x{status.RawResetReason:X8}" + (status.HangStage == 0xFF ? "" : $" · hung in {Stage(status.HangStage)} · PC 0x{status.HangProgramCounter:X8} · LR 0x{status.HangLinkRegister:X8}");
        if (status.ResetReason is 3 or 4 or 5 && _hangRecords.All(item => item.ProgramCounter != status.HangProgramCounter || item.RawReason != status.RawResetReason))
            _hangRecords.Insert(0, new HangRecord(DateTimeOffset.Now, reason, Stage(status.HangStage), status.HangProgramCounter, status.HangLinkRegister, status.RawResetReason, status.UsbdFreeStackWords));

        ReversePuckSummary.IsVisible = false;
        if (!_settingsDirty)
        {
            _baseline = OpenPuckSettingsDraft.FromStatus(status);
            _draft = CloneDraft(_baseline);
            PopulateSettings(_baseline);
        }
        SetProfileVisibility(profile?.Capabilities);
    }

    private void ApplyReversePuckStatus(ReversePuckStatusSnapshot status)
    {
        OverviewTarget.Text = _session.Device?.DisplayName ?? "ReversePuck";
        OverviewBuild.Text = "ReversePuck";
        OverviewProtocol.Text = $"AC · v{status.ProtocolVersion}";
        OverviewRf.Text = status.RadioLinked ? "RF link up" : "No RF link";
        OverviewMode.Text = status.ForwardingDeck ? "Forwarding a Steam Deck" : "Idle";
        OverviewUpdater.Text = "Panel update supported";
        FirmwareGate.Text = "ReversePuck panel firmware updating is available.";
        ReversePuckSummary.IsVisible = true;
        ReversePuckSummary.Text = status.Bonds.Count == 0 ? "No pucks paired." : string.Join(Environment.NewLine, status.Bonds.Select(bond => $"Slot {bond.Slot}: {(bond.Alive ? "live" : "offline")} · {bond.Serial} · PUUID {Convert.ToHexString(bond.PuckUuid)}"));
        SlotStatusList.Children.Clear();
        foreach (var bond in status.Bonds)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(new TextBlock { Text = $"Slot {bond.Slot}: {(bond.Alive ? "live" : "offline")} · {bond.Serial}", VerticalAlignment = VerticalAlignment.Center });
            var remove = new Button { Content = "Remove" };
            remove.Click += async (_, _) =>
            {
                if (await ConfirmAsync("Remove paired puck", $"Forget {bond.Serial} in slot {bond.Slot}? Re-pairing will be required."))
                    await RunCommandAsync($"Removed paired puck slot {bond.Slot}.", token => _client.RemoveReversePuckBondAsync(bond.Slot, token));
            };
            row.Children.Add(remove);
            SlotStatusList.Children.Add(row);
        }
        SetProfileVisibility(_session.Profile?.Capabilities);
    }

    private void SetProfileVisibility(OpenPuckCapabilities? capabilities)
    {
        var puck = capabilities?.HasSettings == true;
        MainTabs.Items.OfType<TabItem>().First(item => Equals(item.Header, "Controller")).IsEnabled = puck;
        MainTabs.Items.OfType<TabItem>().First(item => Equals(item.Header, "Desktop")).IsEnabled = capabilities?.HasLizardMap == true;
        MainTabs.Items.OfType<TabItem>().First(item => Equals(item.Header, "Backup")).IsEnabled = capabilities?.HasBackup == true;
        MainTabs.Items.OfType<TabItem>().First(item => Equals(item.Header, "Diagnostics")).IsEnabled = puck;
    }

    private void PopulateSettings(OpenPuckSettingsDraft draft)
    {
        _updatingControls = true;
        _draft = CloneDraft(draft);
        MouseDivisor.Value = draft.MouseDivisor;
        MouseFriction.Value = draft.MouseFriction;
        RumbleScale.Value = draft.RumbleScale;
        PersistMode.IsChecked = draft.PersistMode;
        Chord1.SelectedIndex = draft.Chords.ElementAtOrDefault(0);
        Chord2.SelectedIndex = draft.Chords.ElementAtOrDefault(1);
        Chord3.SelectedIndex = draft.Chords.ElementAtOrDefault(2);
        SwitchRate.SelectedValue = draft.SwitchProRate;
        SwitchGyro.SelectedValue = draft.SwitchGyroScale;
        LandAll87.IsChecked = draft.LandAll87;
        RenderControllerType();
        ControllerApplyStatus.Text = "Up to date";
        SetSettingsDirty(false);
        _updatingControls = false;
    }

    private OpenPuckSettingsDraft CollectSettings()
    {
        SaveCurrentTypeFields();
        var draft = _draft ?? CloneDraft(_baseline ?? new OpenPuckSettingsDraft());
        return draft with
        {
            MouseDivisor = (byte)(MouseDivisor.Value ?? 1), MouseFriction = (byte)(MouseFriction.Value ?? 0),
            RumbleScale = (byte)(RumbleScale.Value ?? 0), PersistMode = PersistMode.IsChecked == true,
            Chords = [(byte)Math.Max(0, Chord1.SelectedIndex), (byte)Math.Max(0, Chord2.SelectedIndex), (byte)Math.Max(0, Chord3.SelectedIndex)],
            SwitchProRate = Convert.ToByte(SwitchRate.SelectedValue ?? 2), SwitchGyroScale = Convert.ToByte(SwitchGyro.SelectedValue ?? 10),
            PerTypeSettings = draft.PerTypeSettings.Select(row => row.ToArray()).ToArray(), LandAll87 = LandAll87.IsChecked == true
        };
    }

    private async Task ApplyControllerSettingsAsync()
    {
        if (_baseline is null) return;
        var draft = CollectSettings();
        var accepted = new List<byte>();
        try
        {
            await RunBusyAsync(async token =>
            {
                var progress = new Progress<(int Completed, int Total, byte Field)>(item => ControllerApplyStatus.Text = $"Applying field {item.Field} ({item.Completed}/{item.Total})");
                accepted.AddRange(await _client.ApplySettingsAsync(_baseline, draft, progress, token));
                _baseline = CloneDraft(draft);
                _draft = CloneDraft(draft);
                SetSettingsDirty(false);
                ControllerApplyStatus.Text = accepted.Count == 0 ? "No changes" : $"Applied {accepted.Count} field(s)";
            });
        }
        catch (Exception exception)
        {
            ControllerApplyStatus.Text = $"Stopped after accepting [{string.Join(", ", accepted)}]: {FlattenException(exception)}";
            try { _baseline = OpenPuckSettingsDraft.FromStatus(await _client.RefreshPuckAsync(_lifetime.Token)); PopulateSettings(_baseline); } catch { }
        }
    }

    private void RevertControllerSettings() { if (_baseline is not null) PopulateSettings(_baseline); }
    private void MarkSettingsDirty()
    {
        if (_updatingControls) return;
        SetSettingsDirty(true);
        ControllerApplyStatus.Text = "Unsaved changes";
    }

    private async Task LoadLizardAsync()
    {
        if (_session.Profile?.Capabilities.HasLizardMap != true) return;
        await RunBusyAsync(async token => { ReplaceLizard(await _client.LoadLizardMapAsync(token)); _lizardLoaded = true; SetLizardDirty(false); LizardStatus.Text = $"{_lizardBindings.Count} / 32 bindings loaded"; });
    }

    private async Task SaveLizardAsync()
    {
        ReadLizardEditors();
        await RunBusyAsync(async token => { ReplaceLizard(await _client.SaveLizardMapAsync(_lizardBindings, token)); _lizardLoaded = true; SetLizardDirty(false); LizardStatus.Text = $"Saved {_lizardBindings.Count} bindings"; });
    }

    private void ReplaceLizard(IEnumerable<LizardBinding> bindings) { _lizardBindings.Clear(); _lizardBindings.AddRange(bindings); RenderLizardMap(); }

    private void RenderLizardMap()
    {
        LizardList.Children.Clear();
        for (var index = 0; index < _lizardBindings.Count; index++)
        {
            var binding = _lizardBindings[index];
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("40,150,*,150,150,80"), Margin = new Thickness(0, 3) };
            row.Children.Add(new TextBlock { Text = (index + 1).ToString(), VerticalAlignment = VerticalAlignment.Center });
            var type = new ComboBox { Name = "OutputType", ItemsSource = new[] { "Disabled", "Keyboard", "Mouse button", "Mouse move", "Scroll", "Media key" }, SelectedIndex = Math.Min(5, (int)binding.OutputType), Margin = new Thickness(4, 0) };
            Grid.SetColumn(type, 1); row.Children.Add(type);
            var data = new TextBox { Name = "OutputData", Text = Convert.ToHexString(binding.OutputData).ToLowerInvariant(), PlaceholderText = "7-byte output data", Margin = new Thickness(4, 0) };
            Grid.SetColumn(data, 2); row.Children.Add(data);
            var trigger = new TextBox { Name = "TriggerMask", Text = $"0x{binding.TriggerMask:X8}", PlaceholderText = "trigger mask", Margin = new Thickness(4, 0) };
            Grid.SetColumn(trigger, 3); row.Children.Add(trigger);
            var hold = new TextBox { Name = "HoldMask", Text = $"0x{binding.HoldMask:X8}", PlaceholderText = "hold mask", Margin = new Thickness(4, 0) };
            Grid.SetColumn(hold, 4); row.Children.Add(hold);
            type.SelectionChanged += (_, _) => SetLizardDirty(true);
            data.TextChanged += (_, _) => SetLizardDirty(true);
            trigger.TextChanged += (_, _) => SetLizardDirty(true);
            hold.TextChanged += (_, _) => SetLizardDirty(true);
            var remove = new Button { Content = "Delete", Tag = index };
            remove.Click += (_, _) => { ReadLizardEditors(); _lizardBindings.RemoveAt((int)remove.Tag!); RenderLizardMap(); SetLizardDirty(true); };
            Grid.SetColumn(remove, 5); row.Children.Add(remove);
            LizardList.Children.Add(row);
        }
        LizardAdd.IsEnabled = _lizardBindings.Count < 32;
        LizardStatus.Text = $"{_lizardBindings.Count} / 32 bindings · Output data follows firmware lizard_map.h (keyboard: modifiers,key; mouse/axis/scroll/media use their native payload).";
    }

    private void ReadLizardEditors()
    {
        for (var index = 0; index < Math.Min(_lizardBindings.Count, LizardList.Children.Count); index++)
        {
            if (LizardList.Children[index] is not Grid row) continue;
            var type = row.Children.OfType<ComboBox>().Single();
            var boxes = row.Children.OfType<TextBox>().ToArray();
            _lizardBindings[index] = new LizardBinding
            {
                OutputType = (byte)Math.Max(0, type.SelectedIndex),
                OutputData = ParseFixedHex(boxes[0].Text, 7),
                TriggerMask = ParseUInt(boxes[1].Text), HoldMask = ParseUInt(boxes[2].Text)
            };
        }
    }

    private async Task ExportBackupAsync()
    {
        try
        {
            var document = await _backup.ExportAsync(_lizardLoaded ? _lizardBindings : null, _lifetime.Token);
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Export OpenPuck backup", SuggestedFileName = $"openpuck-backup-{DateTime.Now:yyyy-MM-dd-HH-mm-ss}.json",
                FileTypeChoices = [new FilePickerFileType("OpenPuck backup") { Patterns = ["*.json"] }]
            });
            if (file is null) return;
            await using var stream = await file.OpenWriteAsync();
            await using var writer = new StreamWriter(stream);
            await writer.WriteAsync(OpenPuckBackupService.Serialize(document));
            BackupStatus.Text = $"Exported {document.Bonds.Count(bond => bond.Used)} bond(s) and all settings.";
        }
        catch (Exception exception) { BackupStatus.Text = FlattenException(exception); }
    }

    private async Task ImportBackupAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import OpenPuck backup", AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("OpenPuck backup") { Patterns = ["*.json"] }]
        });
        if (files.Count == 0) return;
        try
        {
            await using var stream = await files[0].OpenReadAsync();
            using var reader = new StreamReader(stream);
            var document = OpenPuckBackupService.Deserialize(await reader.ReadToEndAsync(_lifetime.Token));
            if (!await ConfirmAsync("Restore backup", $"Overwrite every setting and {document.Bonds.Count(b => b.Used)} controller pairing(s) on {_session.Device?.SerialNumber}? The target reboots afterward.")) return;
            var progress = new Progress<FirmwareUpdateProgress>(item => { BackupStatus.Text = item.Stage; BackupProgress.Value = item.Percent ?? BackupProgress.Value; });
            await _backup.ImportAsync(document, progress, _lifetime.Token);
            BackupStatus.Text = "Backup restored; waiting up to 40 seconds for the target to return…";
            var reconnected = await _session.ReconnectAfterRebootAsync(TimeSpan.FromSeconds(40), _lifetime.Token);
            if (reconnected) ReflectReconnectedDevice();
            BackupStatus.Text = reconnected ? "Backup restored and target reconnected." : "Restore was sent, but the target did not reconnect. Unplug/replug it.";
        }
        catch (Exception exception) { BackupStatus.Text = FlattenException(exception); }
    }

    private async Task StopCaptureAsync()
    {
        await RunBusyAsync(async token =>
        {
            await _client.StopCaptureAsync(token);
            var frames = await _client.DrainCaptureAsync(token);
            CaptureOutput.Text = FormatCapture(frames);
            AppendDiagnostic($"Capture stopped: {frames.Count} frame(s) drained.");
        });
    }

    private async Task LoadFlightAsync()
    {
        await RunBusyAsync(async token =>
        {
            var frames = await _client.LoadFlightRecorderAsync(token);
            DiagnosticOutput.Text = string.Join(Environment.NewLine, frames.Select(frame => $"A8 {Convert.ToHexString(frame.Payload)}"));
        });
    }

    private async Task SaveHangCsvAsync()
    {
        var csv = new StringBuilder("time,reason,stage,pc,lr,resetreas,usbd_free_words\r\n");
        foreach (var item in _hangRecords) csv.AppendLine($"\"{item.Time:O}\",\"{item.Reason}\",\"{item.Stage}\",0x{item.ProgramCounter:X8},0x{item.LinkRegister:X8},0x{item.RawReason:X8},{item.StackWords}");
        await SaveTextAsync("openpuck-hanglog.csv", csv.ToString(), "CSV", "csv");
    }

    private async Task PickFirmwareAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select OpenPuck UF2", AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("UF2 firmware") { Patterns = ["*.uf2"] }]
        });
        if (files.Count == 0) return;
        try
        {
            await using var stream = await files[0].OpenReadAsync();
            using var memory = new MemoryStream();
            await stream.CopyToAsync(memory, _lifetime.Token);
            _selectedFirmware = Uf2FirmwareParser.Parse(files[0].Name, memory.ToArray());
            FirmwareSelection.Text = $"{_selectedFirmware.Name} · {_selectedFirmware.Data.Length / 1024.0:F1} KiB · CRC32 0x{_selectedFirmware.Crc32:X8}" + (_selectedFirmware.SupportsPanelUpdates ? " · panel-update capable" : " · WARNING: no OPK-FWUP-v1 tag");
            FirmwareFlash.IsEnabled = _session.Profile?.Capabilities.HasFirmwareUpdater == true;
        }
        catch (Exception exception) { FirmwareSelection.Text = "UF2 rejected: " + FlattenException(exception); }
    }

    private async Task FlashFirmwareAsync()
    {
        if (_selectedFirmware is null) return;
        if (!_selectedFirmware.SupportsPanelUpdates && !await ConfirmAsync("Downgrade warning", "This image lacks OPK-FWUP-v1. After flashing, future panel updates may require UF2 DFU drag-and-drop. Continue?")) return;
        if (_selectedFirmware.Name.Contains("factory-reset", StringComparison.OrdinalIgnoreCase) && !await ConfirmAsync("Factory-reset firmware", "This image wipes all settings and controller pairings on first boot. Continue?")) return;
        if (!await ConfirmAsync("Flash firmware", $"Stage, verify, and reboot into {_selectedFirmware.Name}? The running firmware remains untouched until verification succeeds.")) return;
        var progress = new Progress<FirmwareUpdateProgress>(item => { FirmwareStatus.Text = item.Stage; if (item.Percent is int percent) FirmwareProgress.Value = percent; });
        try
        {
            await _firmware.UpdateAsync(_selectedFirmware, progress, _lifetime.Token);
            FirmwareStatus.Text = "Image verified and reboot requested; waiting up to 40 seconds…";
            var returned = await _session.ReconnectAfterRebootAsync(TimeSpan.FromSeconds(40), _lifetime.Token);
            if (returned) ReflectReconnectedDevice();
            FirmwareStatus.Text = returned ? "Update applied and target reconnected." : "Target did not reconnect. Unplug/replug it; if UF2BOOT appears, drag the UF2 onto it for recovery.";
        }
        catch (Exception exception)
        {
            try { await _firmware.AbortAsync(_lifetime.Token); } catch { }
            FirmwareStatus.Text = "Update failed: " + FlattenException(exception) + " The running firmware was not applied.";
        }
    }

    private async Task LoadReleasesAsync()
    {
        try
        {
            FirmwareStatus.Text = "Loading GitHub releases…";
            var releases = await _releases.GetReleasesAsync(_advanced && IncludePrereleases.IsChecked == true, _lifetime.Token);
            ReleaseList.ItemsSource = releases.Select(release => new ReleaseChoice(release)).ToArray();
            FirmwareStatus.Text = $"Loaded {releases.Count} release(s). Double-click a release to select its standard UF2.";
        }
        catch (Exception exception) { FirmwareStatus.Text = FlattenException(exception) + " Local UF2 updating remains available."; }
    }

    private async Task SelectReleaseAsync(bool factoryReset)
    {
        if (ReleaseList.SelectedItem is not ReleaseChoice choice) return;
        var asset = factoryReset ? choice.Release.FactoryResetAsset : choice.Release.StandardAsset;
        if (asset is null)
        {
            FirmwareStatus.Text = factoryReset ? "That release has no factory-reset UF2 asset." : "That release has no standard UF2 asset.";
            return;
        }
        if (factoryReset && !await ConfirmAsync("Factory-reset release", "This firmware wipes all settings and controller pairings on its first boot. Download and select it?")) return;
        try
        {
            FirmwareStatus.Text = $"Downloading {choice.Release.Tag}…";
            var bytes = await _releases.DownloadAsync(asset, _lifetime.Token);
            _selectedFirmware = Uf2FirmwareParser.Parse($"openpuck-{choice.Release.Tag}{(factoryReset ? "-factory-reset" : "")}.uf2", bytes);
            FirmwareSelection.Text = $"{_selectedFirmware.Name} · {_selectedFirmware.Data.Length / 1024.0:F1} KiB · CRC32 0x{_selectedFirmware.Crc32:X8}";
            FirmwareFlash.IsEnabled = _session.Profile?.Capabilities.HasFirmwareUpdater == true;
            FirmwareStatus.Text = "Release image downloaded and validated.";
        }
        catch (Exception exception) { FirmwareStatus.Text = FlattenException(exception); }
    }

    private async Task DestructiveAsync(bool wipe)
    {
        var word = wipe ? "WIPE" : "ERASE";
        var first = wipe ? "This erases the firmware, all settings, and all bonds. The board will boot only as a UF2 drive." : "This erases every setting and controller bond and reboots to defaults.";
        if (!await ConfirmAsync(wipe ? "Full-board wipe" : "Factory erase", first + " This cannot be undone.")) return;
        if (!await ConfirmAsync("Final warning", wipe ? "The only recovery is dragging a valid OpenPuck UF2 onto the mounted UF2 drive. Continue?" : "All pairings will be gone and controllers must be re-paired. Continue?")) return;
        var typed = await PromptAsync("Typed confirmation", $"Type {word} in all capitals to continue:");
        if (typed != word) { AppendDiagnostic($"{word} cancelled: confirmation did not match."); return; }
        await RunCommandAsync(wipe ? "Full-board wipe sent." : "Factory erase sent.", wipe ? _client.WipeBoardAsync : _client.FactoryEraseAsync);
    }

    private async Task ConfirmedRebootAsync(string title, string message, Func<CancellationToken, Task> command)
    {
        if (await ConfirmAsync(title, message)) await RunCommandAsync($"{title} command sent.", command);
    }

    private async Task ToggleStabilityTestAsync()
    {
        if (_busy) return;
        var enable = !_stabilityTestActive;
        try
        {
            await RunBusyAsync(token => _client.SetStabilityTestAsync(enable, token));
            _stabilityTestActive = enable;
            StabilityTest.Content = enable ? "Stop stability test" : "Start stability test";
            AppendDiagnostic(enable ? "Stability test started." : "Stability test stopped.");
        }
        catch (Exception exception)
        {
            AppendDiagnostic(FlattenException(exception));
        }
    }

    private async Task ToggleAdvancedAsync()
    {
        if (!_advanced)
        {
            var accepted = await ConfirmAsync(
                "Enable Advanced mode?",
                "Advanced mode reveals experimental diagnostics, prerelease firmware, factory erase, and full-board wipe. " +
                "These tools can disconnect the target, erase controller pairings and settings, or require UF2 recovery. " +
                "Only continue if you understand those risks. Advanced mode lasts only for this app session.");
            if (!accepted) return;
            _advanced = true;
            AdvancedButton.Content = "Advanced on";
        }
        else
        {
            _advanced = false;
            AdvancedButton.Content = "Advanced";
            IncludePrereleases.IsChecked = false;
        }
        AdvancedDiagnostics.IsVisible = _advanced;
        LandAllPanel.IsVisible = _advanced;
        IncludePrereleases.IsVisible = _advanced;
    }

    private void ReflectReconnectedDevice()
    {
        if (_session.Device is not OpenPuckDevice device) return;
        _updatingDeviceSelection = true;
        try
        {
            DevicePicker.ItemsSource = new[] { device };
            DevicePicker.SelectedItem = device;
            UpdateDeviceDetails();
        }
        finally
        {
            _updatingDeviceSelection = false;
        }
        SetConnectedUi(true);
        ConnectionState.Text = $"Reconnected · {_session.Profile?.Kind} · protocol v{_session.Profile?.ProtocolVersion}";
    }

    private void BuildModeButtons()
    {
        for (var mode = 0; mode < ModeNames.Length; mode++)
        {
            var value = (byte)mode;
            var button = new Button { Content = ModeNames[mode], Tag = value };
            button.Click += async (_, _) =>
            {
                var clean = value is 7 or 8 or 9;
                var warning = clean ? " This clean mode removes the configuration interface; use the controller chord L4+A to return to Steam mode." : "";
                if (await ConfirmAsync("Change USB mode", $"Switch to {ModeNames[value]} and reboot?{warning}"))
                    await RunCommandAsync($"Mode switch to {ModeNames[value]} requested.", token => _client.SwitchModeAsync(value, token));
            };
            ModeButtons.Children.Add(button);
        }
    }

    private void BuildControllerTypeFields()
    {
        for (var index = 0; index < _typeFields.Length; index++)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("250,*,Auto"), Margin = new Thickness(0, 3) };
            row.Children.Add(new TextBlock { Text = MappingNames[index], VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.LightSteelBlue });
            var field = new NumericUpDown { Minimum = 0, Maximum = index == 7 ? 100 : 255 };
            Grid.SetColumn(field, 1); row.Children.Add(field);
            var help = new Button { Content = "?", Margin = new Thickness(8, 0, 0, 0) };
            help.Classes.Add("help");
            ToolTip.SetTip(help, MappingHelp[index]);
            Grid.SetColumn(help, 2); row.Children.Add(help);
            ControllerTypeFields.Children.Add(row);
            _typeFields[index] = field;
        }
    }

    private void PopulateChoiceLists()
    {
        ControllerTypePicker.ItemsSource = ControllerTypes;
        ControllerTypePicker.SelectedIndex = 0;
        foreach (var chord in new[] { Chord1, Chord2, Chord3 }) chord.ItemsSource = ModeNames;
        SwitchRate.ItemsSource = new[] { new Choice<byte>("66 Hz (compatibility)", 0), new Choice<byte>("120 Hz", 1), new Choice<byte>("Full rate (~250 Hz)", 2) };
        SwitchRate.SelectedValueBinding = new Avalonia.Data.Binding(nameof(Choice<byte>.Value));
        SwitchGyro.ItemsSource = new[] { 5, 10, 15, 20, 25, 30 }.Select(value => new Choice<byte>($"{value / 10.0:F1}×", (byte)value)).ToArray();
        SwitchGyro.SelectedValueBinding = new Avalonia.Data.Binding(nameof(Choice<byte>.Value));
    }

    private void RenderControllerType()
    {
        if (_draft is null) return;
        _updatingControls = true;
        var type = Math.Max(0, ControllerTypePicker.SelectedIndex);
        var values = _draft.PerTypeSettings.ElementAtOrDefault(type) ?? new byte[9];
        for (var index = 0; index < 9; index++) _typeFields[index].Value = values.ElementAtOrDefault(index);
        _renderedControllerType = type;
        _updatingControls = false;
    }

    private void SaveCurrentTypeFields()
    {
        if (_updatingControls || _draft is null || _renderedControllerType < 0 || _renderedControllerType >= _draft.PerTypeSettings.Length) return;
        for (var index = 0; index < 9; index++) _draft.PerTypeSettings[_renderedControllerType][index] = (byte)(_typeFields[index].Value ?? 0);
    }

    private static OpenPuckSettingsDraft CloneDraft(OpenPuckSettingsDraft draft) => draft with
    {
        Chords = draft.Chords.ToArray(),
        PerTypeSettings = draft.PerTypeSettings.Select(row => row.ToArray()).ToArray()
    };

    private void SetConnectedUi(bool connected)
    {
        ConnectButton.Content = connected ? "Disconnect" : "Connect";
        DevicePicker.IsEnabled = !connected && !_busy;
        RescanButton.IsEnabled = !connected && !_busy;
        ControllerApply.IsEnabled = connected;
        LizardSave.IsEnabled = connected;
        BackupExport.IsEnabled = connected;
        BackupImport.IsEnabled = connected;
        FirmwarePick.IsEnabled = connected;
        SerialDfu.IsEnabled = connected;
        Uf2Dfu.IsEnabled = connected;
        if (!connected) MainTabs.SelectedIndex = 0;
        SetProfileVisibility(connected ? _session.Profile?.Capabilities : null);
    }

    private bool HasUnsavedChanges => _settingsDirty || _lizardDirty;

    private void SetSettingsDirty(bool dirty)
    {
        _settingsDirty = dirty;
        UpdateUnsavedChangesUi();
    }

    private void SetLizardDirty(bool dirty)
    {
        _lizardDirty = dirty;
        UpdateUnsavedChangesUi();
        if (dirty) LizardStatus.Text = "Unsaved changes";
    }

    private void ClearUnsavedChanges()
    {
        _settingsDirty = false;
        _lizardDirty = false;
        UpdateUnsavedChangesUi();
    }

    private void UpdateUnsavedChangesUi()
    {
        UnsavedChangesText.IsVisible = HasUnsavedChanges;
        Title = "OpenPuck Native Configuration" + (HasUnsavedChanges ? " *" : "");
    }

    private Task<bool> ConfirmDiscardChangesAsync(string action) => ConfirmAsync(
        "Unsaved changes",
        $"You have unsaved changes. Continue to {action} and discard them?");

    private async void OnClosing(object? sender, WindowClosingEventArgs eventArgs)
    {
        if (_allowClose || !HasUnsavedChanges) return;

        eventArgs.Cancel = true;
        if (_closeConfirmationPending) return;

        _closeConfirmationPending = true;
        try
        {
            if (await ConfirmDiscardChangesAsync("close the app"))
            {
                _allowClose = true;
                Close();
            }
        }
        finally
        {
            _closeConfirmationPending = false;
        }
    }

    private void UpdateDeviceDetails()
    {
        DeviceDetails.Text = DevicePicker.SelectedItem is OpenPuckDevice device
            ? $"{device.Product} · VID:PID {device.VendorId:X4}:{device.ProductId:X4} · {device.EndpointSummary}" + (string.IsNullOrWhiteSpace(device.SerialNumber) ? "" : $" · {device.SerialNumber}")
            : "Connect an OpenPuck microcontroller target over USB, then rescan.";
    }

    private async Task RunCommandAsync(string success, Func<CancellationToken, Task> command)
    {
        try { await RunBusyAsync(command); AppendDiagnostic(success); }
        catch (Exception exception) { AppendDiagnostic(FlattenException(exception)); }
    }

    private async Task RunBusyAsync(Func<CancellationToken, Task> operation)
    {
        if (_busy) return;
        _busy = true;
        ConnectButton.IsEnabled = false;
        try { await operation(_lifetime.Token); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception) { ConnectionState.Text = FlattenException(exception); throw; }
        finally
        {
            _busy = false;
            ConnectButton.IsEnabled = true;
            SetConnectedUi(_session.IsConnected);
        }
    }

    private void AppendDiagnostic(string text)
    {
        DiagnosticOutput.Text = $"{DateTime.Now:T}  {text}{Environment.NewLine}" + DiagnosticOutput.Text;
    }

    private async Task SaveTextAsync(string name, string text, string typeName, string extension)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = name,
            FileTypeChoices = [new FilePickerFileType(typeName) { Patterns = [$"*.{extension}"] }]
        });
        if (file is null) return;
        await using var stream = await file.OpenWriteAsync();
        await using var writer = new StreamWriter(stream);
        await writer.WriteAsync(text);
    }

    private async Task<bool> ConfirmAsync(string title, string message)
    {
        var result = false;
        var dialog = new Window { Title = title, Width = 520, SizeToContent = SizeToContent.Height, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var yes = new Button { Content = "Continue", Background = Brushes.Firebrick };
        var no = new Button { Content = "Cancel" };
        yes.Click += (_, _) => { result = true; dialog.Close(); };
        no.Click += (_, _) => dialog.Close();
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(22), Spacing = 16,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { no, yes } }
            }
        };
        await dialog.ShowDialog(this);
        return result;
    }

    private async Task<string?> PromptAsync(string title, string message)
    {
        string? result = null;
        var dialog = new Window { Title = title, Width = 460, SizeToContent = SizeToContent.Height, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var input = new TextBox();
        var ok = new Button { Content = "Confirm", Background = Brushes.Firebrick };
        var cancel = new Button { Content = "Cancel" };
        ok.Click += (_, _) => { result = input.Text; dialog.Close(); };
        cancel.Click += (_, _) => dialog.Close();
        dialog.Content = new StackPanel { Margin = new Thickness(22), Spacing = 12, Children = { new TextBlock { Text = message }, input, new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { cancel, ok } } } };
        await dialog.ShowDialog(this);
        return result;
    }

    private static string FormatCapture(IEnumerable<OpenPuckFrame> frames)
    {
        var lines = new List<string>();
        foreach (var frame in frames.Where(frame => frame.Payload.Length > 0 && frame.Payload[0] == 1 && frame.Payload.Length >= 8))
        {
            var p = frame.Payload;
            var ms = BitConverter.ToUInt32(p, 1);
            var length = Math.Min(p[7], p.Length - 8);
            lines.Add($"{ms,10} ms  slot {p[5]:X2}  report {p[6]:X2}  {Convert.ToHexString(p.AsSpan(8, length)).ToLowerInvariant()}");
        }
        return lines.Count == 0 ? "(nothing captured)" : string.Join(Environment.NewLine, lines);
    }

    private static byte[] ParseFixedHex(string? text, int length)
    {
        var clean = new string((text ?? "").Where(Uri.IsHexDigit).ToArray());
        if (clean.Length % 2 != 0) clean = "0" + clean;
        var parsed = clean.Length == 0 ? [] : Convert.FromHexString(clean);
        var result = new byte[length];
        parsed.AsSpan(0, Math.Min(parsed.Length, length)).CopyTo(result);
        return result;
    }

    private static uint ParseUInt(string? text)
    {
        var value = (text ?? "0").Trim();
        return uint.TryParse(value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? value[2..] : value,
            NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var result) ? result : 0;
    }

    private static string Stage(byte index) => index < StageNames.Length ? StageNames[index] : index == 0xFF ? "—" : $"stage {index}";
    private static string ClockName(byte clock, bool lowFrequency) => lowFrequency
        ? clock switch { 0 => "stopped", 1 => "RC", 2 => "crystal", 3 => "synth", _ => $"code {clock}" }
        : clock switch { 0 => "RC", 2 => "crystal", _ => $"code {clock}" };
    private static string FlattenException(Exception exception) => string.Join(" ", Generate(exception).Distinct());
    private static IEnumerable<string> Generate(Exception exception) { for (Exception? current = exception; current is not null; current = current.InnerException) yield return current.Message; }

    private sealed record Choice<T>(string Label, T Value) { public override string ToString() => Label; }
    private sealed record ReleaseChoice(FirmwareRelease Release) { public override string ToString() => $"{Release.Tag} · {Release.Name}" + (Release.IsPrerelease ? " · prerelease" : ""); }
    private sealed record HangRecord(DateTimeOffset Time, string Reason, string Stage, uint ProgramCounter, uint LinkRegister, uint RawReason, ushort StackWords);
}
