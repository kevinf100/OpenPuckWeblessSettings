using System.Text.Json.Serialization;

namespace OpenPuckWeblessSettings.Usb;

public enum OpenPuckTargetKind
{
    Puck,
    ReversePuck
}

public sealed record OpenPuckCapabilities(
    bool HasSettings,
    bool HasPerControllerSettings,
    bool HasMultiControllerStatus,
    bool HasDiagnostics,
    bool HasFirmwareUpdater,
    bool HasLizardMap,
    bool HasBackup)
{
    public static OpenPuckCapabilities For(OpenPuckTargetKind kind, byte protocolVersion) =>
        kind == OpenPuckTargetKind.ReversePuck
            ? new(false, false, false, false, true, false, false)
            : new(
                HasSettings: true,
                HasPerControllerSettings: protocolVersion >= 10,
                HasMultiControllerStatus: protocolVersion >= 8,
                HasDiagnostics: protocolVersion >= 11,
                HasFirmwareUpdater: protocolVersion >= 15,
                HasLizardMap: protocolVersion >= 16,
                HasBackup: protocolVersion >= 10);
}

public sealed record OpenPuckTargetProfile(
    OpenPuckTargetKind Kind,
    byte ProtocolVersion,
    string BuildId,
    bool DirtyBuild,
    OpenPuckCapabilities Capabilities);

public sealed record ControllerSlotStatus(
    int Slot,
    bool Linked,
    byte BatteryPercent,
    byte RssiMagnitude,
    ushort PollsPerSecond,
    ushort DeliveredPerSecond,
    ushort NewPerSecond,
    byte CrcFailures,
    byte NoRxFailures,
    byte RelayFailures);

public sealed record PuckStatusSnapshot(
    byte ProtocolVersion,
    byte Mode,
    byte MouseDivisor,
    byte MouseFriction,
    bool RadioPeerLinked,
    ushort DeliveredPerSecond,
    ushort NewPerSecond,
    bool PersistMode,
    byte[] Chords,
    ushort PollsPerSecond,
    ushort LoopMicroseconds,
    byte WorstStage,
    ushort WorstStageMicroseconds,
    ushort PollMicroseconds,
    byte BatteryPercent,
    byte RssiMagnitude,
    bool DirtyBuild,
    string BuildId,
    byte RumbleScale,
    byte SwitchProRate,
    byte SwitchGyroScale,
    short AccelX,
    short AccelY,
    short AccelZ,
    IReadOnlyList<ControllerSlotStatus> Slots,
    byte[][] PerTypeSettings,
    byte ResetReason,
    uint RawResetReason,
    byte HangStage,
    uint HangProgramCounter,
    uint HangLinkRegister,
    ushort UsbdFreeStackWords,
    ushort RelayFramesPerSecond,
    byte LowFrequencyClock,
    byte HighFrequencyClock,
    ushort MicrosecondsPerMillisecond,
    byte LoopStage,
    ushort LoopStallMilliseconds,
    byte CrcFailures,
    byte NoRxFailures,
    byte HealCount,
    ushort RingFaults,
    bool LandAll87,
    byte[] RawPayload);

public sealed record ReversePuckBond(
    byte Slot,
    bool Alive,
    byte[] PuckUuid,
    byte[] IdentityUuid,
    string Serial);

public sealed record ReversePuckStatusSnapshot(
    byte ProtocolVersion,
    bool ForwardingDeck,
    bool RadioLinked,
    IReadOnlyList<ReversePuckBond> Bonds,
    byte[] RawPayload);

public sealed record OpenPuckSettingsDraft
{
    public byte Mode { get; init; }
    public byte MouseDivisor { get; init; }
    public byte MouseFriction { get; init; }
    public byte RumbleScale { get; init; }
    public bool PersistMode { get; init; }
    public byte[] Chords { get; init; } = [3, 1, 4];
    public byte SwitchProRate { get; init; } = 2;
    public byte SwitchGyroScale { get; init; } = 10;
    public byte[][] PerTypeSettings { get; init; } = Enumerable.Range(0, 4).Select(_ => new byte[9]).ToArray();
    public bool LandAll87 { get; init; }

    public static OpenPuckSettingsDraft FromStatus(PuckStatusSnapshot status) => new()
    {
        Mode = status.Mode,
        MouseDivisor = status.MouseDivisor,
        MouseFriction = status.MouseFriction,
        RumbleScale = status.RumbleScale,
        PersistMode = status.PersistMode,
        Chords = status.Chords.ToArray(),
        SwitchProRate = status.SwitchProRate,
        SwitchGyroScale = status.SwitchGyroScale,
        PerTypeSettings = status.PerTypeSettings.Select(row => row.ToArray()).ToArray(),
        LandAll87 = status.LandAll87
    };

    public IReadOnlyDictionary<byte, byte> ToFieldMap()
    {
        var fields = new SortedDictionary<byte, byte>
        {
            [1] = MouseDivisor,
            [2] = MouseFriction,
            [16] = PersistMode ? (byte)1 : (byte)0,
            [17] = Chords.ElementAtOrDefault(0),
            [18] = Chords.ElementAtOrDefault(1),
            [19] = Chords.ElementAtOrDefault(2),
            [22] = RumbleScale,
            [23] = SwitchProRate,
            [24] = SwitchGyroScale,
            [29] = LandAll87 ? (byte)1 : (byte)0
        };
        for (var type = 0; type < Math.Min(4, PerTypeSettings.Length); type++)
        {
            for (var index = 0; index < Math.Min(9, PerTypeSettings[type].Length); index++)
            {
                fields[(byte)(40 + type * 9 + index)] = PerTypeSettings[type][index];
            }
        }
        return fields;
    }
}

public sealed record LizardBinding
{
    [JsonPropertyName("outType")]
    public byte OutputType { get; init; }

    [JsonPropertyName("od")]
    public byte[] OutputData { get; init; } = new byte[7];

    [JsonPropertyName("trig")]
    public uint TriggerMask { get; init; }

    [JsonPropertyName("hold")]
    public uint HoldMask { get; init; }
}

public sealed record OpenPuckBackupBond(
    [property: JsonPropertyName("slot")] int Slot,
    [property: JsonPropertyName("used")] bool Used,
    [property: JsonPropertyName("rec")] string Record);

public sealed record OpenPuckBackupType
{
    [JsonPropertyName("back")] public byte[] Back { get; init; } = new byte[4];
    [JsonPropertyName("qam")] public byte Qam { get; init; }
    [JsonPropertyName("abSwap")] public byte AbSwap { get; init; }
    [JsonPropertyName("pad")] public byte Pad { get; init; }
    [JsonPropertyName("led")] public byte Led { get; init; }
    [JsonPropertyName("rumble")] public byte Rumble { get; init; } = 1;
}

public sealed record OpenPuckBackupConfig
{
    [JsonPropertyName("mode")] public byte Mode { get; init; }
    [JsonPropertyName("mDiv")] public byte MouseDivisor { get; init; }
    [JsonPropertyName("mFric")] public byte MouseFriction { get; init; }
    [JsonPropertyName("persistMode")] public byte PersistMode { get; init; }
    [JsonPropertyName("chord")] public byte[] Chords { get; init; } = [3, 1, 4];
    [JsonPropertyName("rumble")] public byte Rumble { get; init; }
    [JsonPropertyName("swProRate")] public byte SwitchProRate { get; init; }
    [JsonPropertyName("swGyro10")] public byte SwitchGyroScale { get; init; }
    [JsonPropertyName("types")] public OpenPuckBackupType[] Types { get; init; } = [];
    [JsonPropertyName("lizardMap")] public LizardBinding[]? LizardMap { get; init; }
}

public sealed record OpenPuckBackupDocument
{
    [JsonPropertyName("magic")] public string Magic { get; init; } = "openpuck-backup";
    [JsonPropertyName("version")] public int Version { get; init; } = 2;
    [JsonPropertyName("bonds")] public OpenPuckBackupBond[] Bonds { get; init; } = [];
    [JsonPropertyName("config")] public OpenPuckBackupConfig Config { get; init; } = new();
}

public sealed record FirmwareImage(string Name, byte[] Data, uint Crc32, bool SupportsPanelUpdates);

public sealed record FirmwareRelease(
    string Tag,
    string Name,
    bool IsPrerelease,
    DateTimeOffset PublishedAt,
    Uri? StandardAsset,
    Uri? FactoryResetAsset);

public sealed record FirmwareUpdateProgress(string Stage, int? Percent, long BytesTransferred, long TotalBytes);

public sealed record FirmwareAcknowledgement(byte Status, uint NextOffset);

