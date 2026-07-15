using OpenPuckWeblessSettings.Usb;

namespace OpenPuckWeblessSettings.Tests;

public sealed class OpenPuckStatusParserTests
{
    [Fact]
    public void ParsesVersion17SettingsSlotsAndDiagnostics()
    {
        var payload = new byte[180];
        payload[0] = 17;
        payload[1] = 4;
        payload[2] = 12;
        payload[3] = 9;
        payload[11] = 1;
        payload[22] = 1;
        payload[23] = 3;
        payload[24] = 1;
        payload[25] = 4;
        payload[38] = 1;
        "1.2.3"u8.CopyTo(payload.AsSpan(39));
        payload[51] = 180;
        payload[52] = 2;
        payload[53] = 15;
        payload[60] = 1;
        payload[61] = 1;
        payload[62] = 88;
        payload[63] = 42;
        payload[73] = 11;
        payload[109] = 3;
        payload[126] = 4;
        payload[143] = 0x34;
        payload[144] = 0x12;
        payload[179] = 1;

        var status = OpenPuckStatusParser.ParsePuck(payload);

        Assert.Equal((byte)17, status.ProtocolVersion);
        Assert.Equal("1.2.3", status.BuildId);
        Assert.True(status.DirtyBuild);
        Assert.True(status.RadioPeerLinked);
        Assert.Single(status.Slots);
        Assert.Equal((ushort)0x1234, status.Slots[0].PollsPerSecond);
        Assert.Equal((byte)11, status.PerTypeSettings[0][0]);
        Assert.True(status.LandAll87);
    }

    [Fact]
    public void ParsesShortOldStatusWithSafeDefaults()
    {
        var payload = new byte[12];
        payload[0] = 7;
        payload[1] = 1;

        var status = OpenPuckStatusParser.ParsePuck(payload);

        Assert.Equal((byte)7, status.ProtocolVersion);
        Assert.Empty(status.Slots);
        Assert.Equal((byte)200, status.RumbleScale);
        Assert.Equal((byte)0xFF, status.HangStage);
    }

    [Fact]
    public void ParsesReversePuckBonds()
    {
        var payload = new byte[29];
        payload[0] = 2;
        payload[1] = 3;
        payload[2] = 1;
        payload[3] = 4;
        payload[4] = 1;
        "PUCK-ONE"u8.CopyTo(payload.AsSpan(13));

        var status = OpenPuckStatusParser.ParseReversePuck(payload);

        Assert.True(status.ForwardingDeck);
        Assert.True(status.RadioLinked);
        Assert.Single(status.Bonds);
        Assert.Equal("PUCK-ONE", status.Bonds[0].Serial);
    }

    [Theory]
    [InlineData(14, false, false)]
    [InlineData(15, true, false)]
    [InlineData(16, true, true)]
    public void PuckCapabilitiesAreVersionGated(byte version, bool updater, bool lizard)
    {
        var capabilities = OpenPuckCapabilities.For(OpenPuckTargetKind.Puck, version);
        Assert.Equal(updater, capabilities.HasFirmwareUpdater);
        Assert.Equal(lizard, capabilities.HasLizardMap);
    }
}

