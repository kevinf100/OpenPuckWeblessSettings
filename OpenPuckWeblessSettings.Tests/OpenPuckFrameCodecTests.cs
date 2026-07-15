using OpenPuckWeblessSettings.Usb;

namespace OpenPuckWeblessSettings.Tests;

public sealed class OpenPuckFrameCodecTests
{
    [Fact]
    public void ParsesConcatenatedFramesAndUnknownTail()
    {
        var frames = OpenPuckFrameCodec.Parse([0x99, 0xA9, 3, 4, 0x58, 0x02, 0xA5, 3, 17, 1, 2, 0xEE]);

        Assert.Equal(2, frames.Count);
        Assert.Equal(0xA9, frames[0].Marker);
        Assert.Equal([4, 0x58, 0x02], frames[0].Payload);
        Assert.Equal(0xA5, frames[1].Marker);
    }

    [Fact]
    public void FindLastCollapsesStaleFirmwareAcknowledgements()
    {
        var frame = OpenPuckFrameCodec.FindLast(
            [0xAB, 5, 0, 0, 0, 0, 0, 0xAB, 5, 0, 0x80, 0, 0, 0], 0xAB, 5);

        Assert.Equal(0x80, frame.Payload[1]);
    }

    [Fact]
    public void RejectsTruncatedKnownFrame()
    {
        Assert.Throws<InvalidDataException>(() => OpenPuckFrameCodec.Parse([0xA7, 5, 1, 2]));
    }

    [Fact]
    public void ParsesLizardMap()
    {
        var data = new byte[18];
        data[0] = 0xAA;
        data[1] = 1;
        data[2] = 1;
        data[3] = 2;
        data[10] = 0x34;
        data[11] = 0x12;

        var map = OpenPuckFrameCodec.ParseLizardMap(data);

        Assert.Single(map);
        Assert.Equal((byte)1, map[0].OutputType);
        Assert.Equal((uint)0x1234, map[0].TriggerMask);
    }
}

