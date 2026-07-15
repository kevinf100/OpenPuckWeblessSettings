namespace OpenPuckWeblessSettings.Usb;

public sealed record OpenPuckFrame(byte Marker, byte[] Payload)
{
    public int Length => Payload.Length + 2;
}

public static class OpenPuckFrameCodec
{
    private static readonly HashSet<byte> KnownMarkers = [0xA5, 0xA6, 0xA7, 0xA8, 0xA9, 0xAB, 0xAC];

    public static IReadOnlyList<OpenPuckFrame> Parse(ReadOnlySpan<byte> data)
    {
        var frames = new List<OpenPuckFrame>();
        for (var offset = 0; offset + 2 <= data.Length;)
        {
            if (!KnownMarkers.Contains(data[offset]))
            {
                offset++;
                continue;
            }
            var length = data[offset + 1];
            if (offset + 2 + length > data.Length)
            {
                throw new InvalidDataException(
                    $"Truncated 0x{data[offset]:X2} frame: declared {length} payload bytes, received {data.Length - offset - 2}.");
            }
            frames.Add(new OpenPuckFrame(data[offset], data.Slice(offset + 2, length).ToArray()));
            offset += 2 + length;
        }
        return frames;
    }

    public static OpenPuckFrame FindLast(ReadOnlySpan<byte> data, byte marker, int minimumPayloadLength = 0)
    {
        var frame = Parse(data).LastOrDefault(candidate => candidate.Marker == marker && candidate.Payload.Length >= minimumPayloadLength);
        return frame ?? throw new InvalidDataException($"No complete 0x{marker:X2} frame was present in the USB reply.");
    }

    public static IReadOnlyList<LizardBinding> ParseLizardMap(ReadOnlySpan<byte> data)
    {
        var marker = data.IndexOf((byte)0xAA);
        if (marker < 0 || marker + 2 > data.Length) throw new InvalidDataException("No 0xAA lizard-map frame was present.");
        var count = data[marker + 1];
        var total = marker + 2 + count * 16;
        if (total > data.Length) throw new InvalidDataException("The 0xAA lizard-map frame is truncated.");
        var result = new List<LizardBinding>(count);
        for (var index = 0; index < count; index++)
        {
            var p = data.Slice(marker + 2 + index * 16, 16);
            result.Add(new LizardBinding
            {
                OutputType = p[0],
                OutputData = p.Slice(1, 7).ToArray(),
                TriggerMask = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(p[8..]),
                HoldMask = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(p[12..])
            });
        }
        return result;
    }
}
