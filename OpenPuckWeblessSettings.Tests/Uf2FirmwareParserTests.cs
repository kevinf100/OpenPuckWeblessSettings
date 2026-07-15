using System.Buffers.Binary;
using OpenPuckWeblessSettings.Services;

namespace OpenPuckWeblessSettings.Tests;

public sealed class Uf2FirmwareParserTests
{
    [Fact]
    public void ExtractsImageFillsGapsAndFindsCapabilityTag()
    {
        var first = BuildBlock(0x26000, "OPK-FWUP-v1"u8.ToArray(), 0, 2);
        var second = BuildBlock(0x26100, [1, 2, 3, 4], 1, 2);

        var image = Uf2FirmwareParser.Parse("test.uf2", first.Concat(second).ToArray());

        Assert.True(image.SupportsPanelUpdates);
        Assert.Equal(260, image.Data.Length);
        Assert.All(image.Data[11..256], value => Assert.Equal((byte)0xFF, value));
        Assert.Equal(Uf2FirmwareParser.Crc32(image.Data), image.Crc32);
    }

    [Fact]
    public void RejectsWrongFamilyAndWrongBase()
    {
        var family = BuildBlock(0x26000, [1, 2, 3, 4], 0, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(family.AsSpan(28), 0x12345678);
        Assert.Throws<InvalidDataException>(() => Uf2FirmwareParser.Parse("bad.uf2", family));

        var basis = BuildBlock(0x27000, [1, 2, 3, 4], 0, 1);
        Assert.Throws<InvalidDataException>(() => Uf2FirmwareParser.Parse("bad.uf2", basis));
    }

    [Fact]
    public void Crc32MatchesStandardVector()
    {
        Assert.Equal(0xCBF43926u, Uf2FirmwareParser.Crc32("123456789"u8));
    }

    private static byte[] BuildBlock(uint address, byte[] payload, uint blockNumber, uint blockCount)
    {
        var block = new byte[512];
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(0), 0x0A324655);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(4), 0x9E5D5157);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(8), 0x2000);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(12), address);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(16), (uint)payload.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(20), blockNumber);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(24), blockCount);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(28), 0xADA52840);
        payload.CopyTo(block, 32);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(508), 0x0AB16F30);
        return block;
    }
}
