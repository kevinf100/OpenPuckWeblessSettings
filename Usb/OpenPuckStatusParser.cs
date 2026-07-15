using System.Buffers.Binary;
using System.Text;

namespace OpenPuckWeblessSettings.Usb;

public static class OpenPuckStatusParser
{
    public static PuckStatusSnapshot ParsePuck(ReadOnlySpan<byte> p)
    {
        if (p.Length < 12)
        {
            throw new InvalidDataException("The A5 status payload is shorter than 12 bytes.");
        }

        var data = p.ToArray();
        byte At(int index, byte fallback = 0) => index < data.Length ? data[index] : fallback;
        ushort U16(int index) => index + 1 < data.Length ? BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(index)) : (ushort)0;
        uint U32(int index) => index + 3 < data.Length ? BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(index)) : 0;
        short S16(int index) => index + 1 < data.Length ? BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(index)) : (short)0;

        var buildBytes = p.Length > 39 ? p.Slice(39, Math.Min(12, p.Length - 39)) : [];
        var zero = buildBytes.IndexOf((byte)0);
        if (zero >= 0) buildBytes = buildBytes[..zero];
        var build = Encoding.Latin1.GetString(buildBytes);

        var perType = new byte[4][];
        for (var type = 0; type < 4; type++)
        {
            var start = 73 + type * 9;
            perType[type] = start + 9 <= p.Length ? p.Slice(start, 9).ToArray() : new byte[9];
        }

        var slots = new List<ControllerSlotStatus>();
        var bondedCount = At(60);
        for (var slot = 0; slot < Math.Min(4, (int)bondedCount); slot++)
        {
            var baseIndex = 61 + slot * 3;
            var statsIndex = 143 + slot * 9;
            slots.Add(new ControllerSlotStatus(
                slot,
                At(baseIndex) != 0,
                At(baseIndex + 1),
                At(baseIndex + 2),
                U16(statsIndex),
                U16(statsIndex + 2),
                U16(statsIndex + 4),
                At(statsIndex + 6),
                At(statsIndex + 7),
                At(statsIndex + 8)));
        }

        return new PuckStatusSnapshot(
            At(0), At(1), At(2), At(3), At(11) != 0, U16(12), U16(15), At(22) != 0,
            [At(23, 3), At(24, 1), At(25, 4)], U16(26), U16(28), At(30), U16(31), U16(33),
            At(36), At(37), At(38) != 0, build, At(51, 200), At(52, 2), At(53, 10),
            S16(54), S16(56), S16(58), slots, perType, At(109), U32(110), At(126, 0xFF),
            U32(131), U32(135), U16(139), U16(120), At(122), At(123), U16(124), At(127),
            (ushort)(At(128) * 40), At(117), At(118), At(119), U16(129), At(179) != 0, p.ToArray());
    }

    public static ReversePuckStatusSnapshot ParseReversePuck(ReadOnlySpan<byte> p)
    {
        if (p.Length < 3) throw new InvalidDataException("The AC status payload is shorter than 3 bytes.");
        var bonds = new List<ReversePuckBond>();
        for (var index = 0; index < p[2]; index++)
        {
            var offset = 3 + index * 26;
            if (offset + 26 > p.Length) break;
            var serial = Encoding.Latin1.GetString(p.Slice(offset + 10, 16)).TrimEnd('\0').Trim();
            bonds.Add(new ReversePuckBond(
                p[offset], p[offset + 1] != 0, p.Slice(offset + 2, 4).ToArray(),
                p.Slice(offset + 6, 4).ToArray(), serial));
        }
        return new ReversePuckStatusSnapshot(p[0], (p[1] & 1) != 0, (p[1] & 2) != 0, bonds, p.ToArray());
    }
}
