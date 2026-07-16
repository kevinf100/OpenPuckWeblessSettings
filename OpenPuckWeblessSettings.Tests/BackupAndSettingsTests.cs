using System.Text.Json;
using OpenPuckWeblessSettings.Services;
using OpenPuckWeblessSettings.Usb;

namespace OpenPuckWeblessSettings.Tests;

public sealed class BackupAndSettingsTests
{
    [Fact]
    public void ImportsLegacyWebV1Fixture()
    {
        var backup = OpenPuckBackupService.Deserialize(Fixture("web-backup-v1.json"));

        Assert.Equal(1, backup.Version);
        Assert.Equal([true, false, false, false], backup.Bonds.Select(bond => bond.Used));
        Assert.Equal(new string('a', 48), backup.Bonds[0].Record);
        Assert.Equal((byte)2, backup.Config.Mode);
        Assert.Equal((byte)16, backup.Config.MouseDivisor);
        Assert.Equal((byte)5, backup.Config.MouseFriction);
        Assert.Equal((byte)1, backup.Config.PersistMode);
        Assert.Equal([3, 1, 4], backup.Config.Chords);
        Assert.Equal((byte)180, backup.Config.Rumble);
        Assert.Equal((byte)3, backup.Config.SwitchProRate);
        Assert.Equal((byte)12, backup.Config.SwitchGyroScale);
        Assert.Empty(backup.Config.Types);
        Assert.Null(backup.Config.LizardMap);
    }

    [Fact]
    public void ImportsCompleteWebV2Fixture()
    {
        var backup = OpenPuckBackupService.Deserialize(Fixture("web-backup-v2.json"));

        Assert.Equal(2, backup.Version);
        Assert.Equal([true, false, true, false], backup.Bonds.Select(bond => bond.Used));
        Assert.Equal(new string('b', 48), backup.Bonds[0].Record);
        Assert.Equal(new string('c', 48), backup.Bonds[2].Record);
        Assert.Equal((byte)4, backup.Config.Mode);
        Assert.Equal((byte)12, backup.Config.MouseDivisor);
        Assert.Equal((byte)8, backup.Config.MouseFriction);
        Assert.Equal((byte)1, backup.Config.PersistMode);
        Assert.Equal([7, 8, 9], backup.Config.Chords);
        Assert.Equal((byte)200, backup.Config.Rumble);
        Assert.Equal((byte)2, backup.Config.SwitchProRate);
        Assert.Equal((byte)10, backup.Config.SwitchGyroScale);
        Assert.Equal(4, backup.Config.Types.Length);
        Assert.Equal([1, 2, 3, 4], backup.Config.Types[0].Back);
        Assert.Equal((byte)11, backup.Config.Types[0].Qam);
        Assert.Equal((byte)1, backup.Config.Types[0].AbSwap);
        Assert.Equal((byte)0, backup.Config.Types[0].Pad);
        Assert.Equal((byte)96, backup.Config.Types[0].Led);
        Assert.Equal((byte)1, backup.Config.Types[0].Rumble);
        var binding = Assert.Single(backup.Config.LizardMap!);
        Assert.Equal((byte)1, binding.OutputType);
        Assert.Equal([0, 4, 0, 0, 0, 0, 0], binding.OutputData);
        Assert.Equal(0x80000001u, binding.TriggerMask);
        Assert.Equal(0x00000002u, binding.HoldMask);
    }

    [Fact]
    public void ImportsBackupFromOlderNativeBase64Serializer()
    {
        var backup = OpenPuckBackupService.Deserialize(Fixture("native-backup-v2-base64.json"));

        Assert.Equal([3, 1, 4], backup.Config.Chords);
        Assert.Equal([1, 2, 3, 4], backup.Config.Types[0].Back);
        Assert.Equal([0, 4, 0, 0, 0, 0, 0], backup.Config.LizardMap![0].OutputData);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, true)]
    public void NativeSerializationMatchesWebImportContract(int version, bool includeLizardMap)
    {
        var json = OpenPuckBackupService.Serialize(CreateBackup(version, includeLizardMap));
        using var parsed = JsonDocument.Parse(json);
        var root = parsed.RootElement;

        Assert.Equal("openpuck-backup", root.GetProperty("magic").GetString());
        Assert.Equal(version, root.GetProperty("version").GetInt32());
        Assert.Equal(4, root.GetProperty("bonds").GetArrayLength());
        AssertPropertyNames(root.GetProperty("bonds")[0], "rec", "slot", "used");

        var config = root.GetProperty("config");
        AssertPropertyNames(config, "chord", "lizardMap", "mDiv", "mFric", "mode", "persistMode",
            "rumble", "swGyro10", "swProRate", "types");
        Assert.Equal(3, config.GetProperty("chord").GetArrayLength());
        Assert.Equal(4, config.GetProperty("types").GetArrayLength());
        AssertPropertyNames(config.GetProperty("types")[0], "abSwap", "back", "led", "pad", "qam", "rumble");
        if (includeLizardMap)
        {
            var binding = config.GetProperty("lizardMap")[0];
            AssertPropertyNames(binding, "hold", "od", "outType", "trig");
        }
        else
        {
            Assert.Equal(JsonValueKind.Null, config.GetProperty("lizardMap").ValueKind);
        }

        Assert.DoesNotContain("Magic", json, StringComparison.Ordinal);
        Assert.DoesNotContain("MouseDivisor", json, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsMapUsesExactFieldNumbers()
    {
        var draft = new OpenPuckSettingsDraft
        {
            MouseDivisor = 9, MouseFriction = 4, PersistMode = true, Chords = [3, 1, 4],
            RumbleScale = 180, SwitchProRate = 2, SwitchGyroScale = 15,
            PerTypeSettings = Enumerable.Range(0, 4).Select(type => Enumerable.Range(0, 9).Select(index => (byte)(type * 10 + index)).ToArray()).ToArray(),
            LandAll87 = true
        };

        var fields = draft.ToFieldMap();

        Assert.Equal((byte)9, fields[1]);
        Assert.Equal((byte)1, fields[16]);
        Assert.Equal((byte)180, fields[22]);
        Assert.Equal((byte)0, fields[40]);
        Assert.Equal((byte)38, fields[75]);
        Assert.Equal((byte)1, fields[29]);
    }

    [Fact]
    public void RejectsMalformedBondRecord()
    {
        var document = new OpenPuckBackupDocument
        {
            Bonds = [new(0, true, "12"), new(1, false, ""), new(2, false, ""), new(3, false, "")]
        };
        Assert.Throws<InvalidDataException>(() => OpenPuckBackupService.Validate(document));
    }

    private static OpenPuckBackupDocument CreateBackup(int version, bool includeLizardMap) => new()
    {
        Version = version,
        Bonds = Enumerable.Range(0, 4)
            .Select(slot => new OpenPuckBackupBond(slot, slot == 0, slot == 0 ? new string('d', 48) : ""))
            .ToArray(),
        Config = new OpenPuckBackupConfig
        {
            Mode = 4,
            MouseDivisor = 12,
            MouseFriction = 8,
            PersistMode = 1,
            Chords = [3, 1, 4],
            Rumble = 200,
            SwitchProRate = 2,
            SwitchGyroScale = 10,
            Types = Enumerable.Range(0, 4).Select(type => new OpenPuckBackupType
            {
                Back = [(byte)(type + 1), 2, 3, 4],
                Qam = 11,
                AbSwap = 1,
                Pad = 0,
                Led = 96,
                Rumble = 1
            }).ToArray(),
            LizardMap = includeLizardMap
                ? [new LizardBinding { OutputType = 1, OutputData = [0, 4, 0, 0, 0, 0, 0], TriggerMask = 1, HoldMask = 2 }]
                : null
        }
    };

    private static string Fixture(string name)
    {
        var resourceName = $"{typeof(BackupAndSettingsTests).Namespace}.Fixtures.{name}";
        using var stream = typeof(BackupAndSettingsTests).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Missing embedded fixture {resourceName}.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static void AssertPropertyNames(JsonElement element, params string[] expected)
    {
        var actual = element.EnumerateObject().Select(property => property.Name).Order().ToArray();
        Assert.Equal(expected.Order().ToArray(), actual);
    }
}
