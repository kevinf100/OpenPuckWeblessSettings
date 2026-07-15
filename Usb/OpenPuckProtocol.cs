namespace OpenPuckWeblessSettings.Usb;

public static class OpenPuckProtocol
{
    public const byte GetStatusCommand = 0x01;
    private const byte PuckStatusMarker = 0xA5;
    private const byte ReversePuckStatusMarker = 0xAC;

    public static ConnectionProbeResult ParseStatusFrame(ReadOnlySpan<byte> data)
    {
        for (var offset = 0; offset + 2 <= data.Length; offset++)
        {
            var marker = data[offset];
            if (marker is not (PuckStatusMarker or ReversePuckStatusMarker))
            {
                continue;
            }

            var payloadLength = data[offset + 1];
            var frameLength = payloadLength + 2;
            if (offset + frameLength > data.Length)
            {
                throw new InvalidDataException(
                    $"Truncated 0x{marker:X2} reply: declared {payloadLength} payload bytes, " +
                    $"received {data.Length - offset - 2}.");
            }

            var payload = data.Slice(offset + 2, payloadLength);
            if (marker == PuckStatusMarker)
            {
                if (payload.Length < 12)
                {
                    throw new InvalidDataException("The A5 status payload is shorter than the required 12 bytes.");
                }

                return new ConnectionProbeResult(
                    OpenPuckReplyType.PuckStatus,
                    payload[0],
                    payload[11] != 0,
                    frameLength);
            }

            if (payload.Length < 3)
            {
                throw new InvalidDataException("The AC status payload is shorter than the required 3 bytes.");
            }

            return new ConnectionProbeResult(
                OpenPuckReplyType.ReversePuckStatus,
                payload[0],
                (payload[1] & 0x02) != 0,
                frameLength);
        }

        throw new InvalidDataException("No A5 or AC OpenPuck status frame was present in the USB reply.");
    }
}
