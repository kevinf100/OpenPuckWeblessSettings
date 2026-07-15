namespace OpenPuckWeblessSettings.Usb;

public enum OpenPuckReplyType
{
    PuckStatus,
    ReversePuckStatus
}

public sealed record ConnectionProbeResult(
    OpenPuckReplyType ReplyType,
    byte ProtocolVersion,
    bool RadioPeerLinked,
    int FrameLength)
{
    public string ReplyName => ReplyType == OpenPuckReplyType.PuckStatus ? "A5 puck status" : "AC ReversePuck status";
}
