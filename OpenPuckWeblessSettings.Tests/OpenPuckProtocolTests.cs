using OpenPuckWeblessSettings.Usb;

namespace OpenPuckWeblessSettings.Tests;

public sealed class OpenPuckProtocolTests
{
    [Fact]
    public void ParsesLinkedA5StatusAfterPrefixBytes()
    {
        var frame = new byte[]
        {
            0x00, 0x99,
            0xA5, 12,
            16, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2, 1
        };

        var result = OpenPuckProtocol.ParseStatusFrame(frame);

        Assert.Equal(OpenPuckReplyType.PuckStatus, result.ReplyType);
        Assert.Equal(16, result.ProtocolVersion);
        Assert.True(result.RadioPeerLinked);
        Assert.Equal(14, result.FrameLength);
    }

    [Fact]
    public void ParsesUnlinkedA5Status()
    {
        var payload = new byte[12];
        payload[0] = 9;
        payload[11] = 0;
        var frame = new byte[] { 0xA5, 12 }.Concat(payload).ToArray();

        var result = OpenPuckProtocol.ParseStatusFrame(frame);

        Assert.False(result.RadioPeerLinked);
        Assert.Equal((byte)9, result.ProtocolVersion);
    }

    [Fact]
    public void ParsesLinkedReversePuckStatus()
    {
        var result = OpenPuckProtocol.ParseStatusFrame([0xAC, 3, 2, 0x02, 1]);

        Assert.Equal(OpenPuckReplyType.ReversePuckStatus, result.ReplyType);
        Assert.Equal((byte)2, result.ProtocolVersion);
        Assert.True(result.RadioPeerLinked);
    }

    [Fact]
    public void RejectsTruncatedFrame()
    {
        Assert.Throws<InvalidDataException>(() =>
            OpenPuckProtocol.ParseStatusFrame([0xA5, 12, 1, 2, 3]));
    }

    [Theory]
    [InlineData(new byte[] { 0xA5, 2, 1, 0 })]
    [InlineData(new byte[] { 0xAC, 2, 1, 0 })]
    public void RejectsUndersizedPayload(byte[] frame)
    {
        Assert.Throws<InvalidDataException>(() => OpenPuckProtocol.ParseStatusFrame(frame));
    }

    [Fact]
    public void RejectsReplyWithoutKnownMarker()
    {
        Assert.Throws<InvalidDataException>(() =>
            OpenPuckProtocol.ParseStatusFrame([0xA7, 3, 1, 2, 3]));
    }
}
