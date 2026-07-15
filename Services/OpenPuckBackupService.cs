using System.Text.Json;
using OpenPuckWeblessSettings.Usb;

namespace OpenPuckWeblessSettings.Services;

public sealed class OpenPuckBackupService(OpenPuckClient client)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public async Task<OpenPuckBackupDocument> ExportAsync(
        IReadOnlyList<LizardBinding>? lizardMap,
        CancellationToken cancellationToken)
    {
        var status = await client.RefreshPuckAsync(cancellationToken);
        var bondPayload = await client.ExportBondsAsync(cancellationToken);
        if (bondPayload.Length < 98) throw new InvalidDataException("The bond export is shorter than 98 bytes.");
        var mask = bondPayload[1];
        var bonds = new OpenPuckBackupBond[4];
        for (var slot = 0; slot < 4; slot++)
        {
            bonds[slot] = new OpenPuckBackupBond(slot, ((mask >> slot) & 1) != 0,
                Convert.ToHexString(bondPayload.AsSpan(2 + slot * 24, 24)).ToLowerInvariant());
        }
        var types = status.PerTypeSettings.Select(row => new OpenPuckBackupType
        {
            Back = row[..4], Qam = row[4], AbSwap = row[5], Pad = row[6], Led = row[7], Rumble = row[8]
        }).ToArray();
        return new OpenPuckBackupDocument
        {
            Version = lizardMap is null ? 1 : 2,
            Bonds = bonds,
            Config = new OpenPuckBackupConfig
            {
                Mode = status.Mode,
                MouseDivisor = status.MouseDivisor,
                MouseFriction = status.MouseFriction,
                PersistMode = status.PersistMode ? (byte)1 : (byte)0,
                Chords = status.Chords,
                Rumble = status.RumbleScale,
                SwitchProRate = status.SwitchProRate,
                SwitchGyroScale = status.SwitchGyroScale,
                Types = types,
                LizardMap = lizardMap?.ToArray()
            }
        };
    }

    public static string Serialize(OpenPuckBackupDocument document) => JsonSerializer.Serialize(document, JsonOptions);

    public static OpenPuckBackupDocument Deserialize(string json)
    {
        var document = JsonSerializer.Deserialize<OpenPuckBackupDocument>(json, JsonOptions)
            ?? throw new InvalidDataException("The backup JSON is empty.");
        Validate(document);
        return document;
    }

    public static void Validate(OpenPuckBackupDocument document)
    {
        if (document.Magic != "openpuck-backup") throw new InvalidDataException("This is not an OpenPuck backup.");
        if (document.Version is < 1 or > 2) throw new InvalidDataException($"Unsupported backup version {document.Version}.");
        if (document.Bonds.Length != 4) throw new InvalidDataException("An OpenPuck backup must contain exactly four bond slots.");
        if (document.Bonds.Select(bond => bond.Slot).Distinct().Count() != 4 || document.Bonds.Any(bond => bond.Slot is < 0 or > 3))
            throw new InvalidDataException("Backup bond slot numbers must be unique and between 0 and 3.");
        foreach (var bond in document.Bonds.Where(bond => bond.Used))
        {
            if (bond.Record.Length != 48 || !bond.Record.All(Uri.IsHexDigit))
                throw new InvalidDataException($"Bond slot {bond.Slot} must contain exactly 24 bytes of hexadecimal data.");
        }
        if (document.Config.Chords.Length < 3) throw new InvalidDataException("The backup chord array is incomplete.");
        if (document.Config.LizardMap?.Length > 32) throw new InvalidDataException("A lizard map cannot contain more than 32 bindings.");
    }

    public async Task ImportAsync(
        OpenPuckBackupDocument document,
        IProgress<FirmwareUpdateProgress>? progress,
        CancellationToken cancellationToken)
    {
        Validate(document);
        var session = client.Session;
        await session.ExclusiveAsync(async token =>
        {
            // Capability probe: old firmware silently ignores bond import.
            await session.WriteRawAsync([0x09], token);
            await session.ReadFrameCoreAsync(0xA7, 98, TimeSpan.FromSeconds(3), 256, token);

            var fields = BuildFields(document.Config).ToArray();
            var map = document.Config.LizardMap?.Take(32).ToArray() ?? [];
            var total = fields.Length + map.Length + 5;
            var completed = 0;
            void Report(string stage)
            {
                var percent = total == 0 ? 100 : Math.Min(99, completed * 100 / total);
                progress?.Report(new FirmwareUpdateProgress(stage, percent, completed, total));
            }

            foreach (var field in fields)
            {
                Report("Restoring settings");
                await session.WriteRawAsync([0x02, field.Key, field.Value], token);
                await session.ReadFrameCoreAsync(0xA5, 12, TimeSpan.FromSeconds(2), 256, token);
                completed++;
            }

            if (map.Length > 0)
            {
                await session.WriteRawAsync([(byte)0x13, (byte)map.Length], token);
                for (var index = 0; index < map.Length; index++)
                {
                    Report("Restoring desktop map");
                    await session.WriteRawAsync(EncodeLizardBinding(index, map[index]), token);
                    completed++;
                }
                await session.WriteRawAsync([0x14], token);
                await ReadLizardEchoAsync(session, token);
            }

            foreach (var slot in Enumerable.Range(0, 4))
            {
                Report("Restoring controller pairings");
                var bond = document.Bonds.Single(candidate => candidate.Slot == slot);
                var record = bond.Used ? Convert.FromHexString(bond.Record) : new byte[24];
                var command = new byte[27];
                command[0] = 0x0D;
                command[1] = (byte)slot;
                command[2] = bond.Used ? (byte)1 : (byte)0;
                record.CopyTo(command, 3);
                await session.WriteRawAsync(command, token);
                await session.ReadFrameCoreAsync(0xA5, 12, TimeSpan.FromSeconds(2), 256, token);
                completed++;
            }
            progress?.Report(new FirmwareUpdateProgress("Committing and rebooting", 99, ++completed, total));
            await session.WriteRawAsync([0x0E, document.Config.Mode], token);
        }, cancellationToken);
    }

    private static IEnumerable<KeyValuePair<byte, byte>> BuildFields(OpenPuckBackupConfig config)
    {
        yield return new(1, config.MouseDivisor);
        yield return new(2, config.MouseFriction);
        yield return new(22, config.Rumble);
        yield return new(16, config.PersistMode == 0 ? (byte)0 : (byte)1);
        yield return new(17, config.Chords[0]);
        yield return new(18, config.Chords[1]);
        yield return new(19, config.Chords[2]);
        yield return new(23, config.SwitchProRate);
        yield return new(24, config.SwitchGyroScale);
        for (var type = 0; type < Math.Min(4, config.Types.Length); type++)
        {
            var item = config.Types[type];
            for (var index = 0; index < 4; index++) yield return new((byte)(40 + type * 9 + index), item.Back.ElementAtOrDefault(index));
            yield return new((byte)(44 + type * 9), item.Qam);
            yield return new((byte)(45 + type * 9), item.AbSwap == 0 ? (byte)0 : (byte)1);
            yield return new((byte)(46 + type * 9), item.Pad == 0 ? (byte)0 : (byte)1);
            yield return new((byte)(47 + type * 9), item.Led);
            yield return new((byte)(48 + type * 9), item.Rumble == 0 ? (byte)0 : (byte)1);
        }
    }

    private static byte[] EncodeLizardBinding(int index, LizardBinding binding)
    {
        var result = new byte[18];
        result[0] = 0x12;
        result[1] = (byte)index;
        result[2] = binding.OutputType;
        binding.OutputData.AsSpan(0, Math.Min(7, binding.OutputData.Length)).CopyTo(result.AsSpan(3, 7));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(10), binding.TriggerMask);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(14), binding.HoldMask);
        return result;
    }

    private static async Task ReadLizardEchoAsync(OpenPuckSession session, CancellationToken token)
    {
        var bytes = new List<byte>();
        for (var count = 0; count < 64; count++)
        {
            bytes.AddRange(await session.ReadRawAsync(256, TimeSpan.FromSeconds(2), token));
            var marker = bytes.IndexOf(0xAA);
            if (marker >= 0 && marker + 2 <= bytes.Count && bytes.Count >= marker + 2 + bytes[marker + 1] * 16) return;
        }
        throw new TimeoutException("Timed out waiting for the restored lizard-map echo.");
    }
}

