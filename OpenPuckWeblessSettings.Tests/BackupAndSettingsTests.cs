using OpenPuckWeblessSettings.Services;
using OpenPuckWeblessSettings.Usb;

namespace OpenPuckWeblessSettings.Tests;

public sealed class BackupAndSettingsTests
{
    [Fact]
    public void BackupRoundTripsUsingWebpagePropertyNames()
    {
        var document = new OpenPuckBackupDocument
        {
            Bonds = Enumerable.Range(0, 4).Select(slot => new OpenPuckBackupBond(slot, slot == 0, slot == 0 ? new string('a', 48) : "")).ToArray(),
            Config = new OpenPuckBackupConfig
            {
                Mode = 4, MouseDivisor = 12, MouseFriction = 8, PersistMode = 1,
                Chords = [3, 1, 4], Rumble = 200, SwitchProRate = 2, SwitchGyroScale = 10,
                Types = [new OpenPuckBackupType { Back = [1, 2, 3, 4], Qam = 11, Pad = 1, Rumble = 1 }],
                LizardMap = [new LizardBinding { OutputType = 1, OutputData = [0, 4, 0, 0, 0, 0, 0], TriggerMask = 1 }]
            }
        };

        var json = OpenPuckBackupService.Serialize(document);
        var restored = OpenPuckBackupService.Deserialize(json);

        Assert.Contains("\"mDiv\"", json);
        Assert.Contains("\"lizardMap\"", json);
        Assert.Equal(document.Config.Mode, restored.Config.Mode);
        Assert.Equal(document.Config.Chords, restored.Config.Chords);
        Assert.Equal(document.Bonds.Select(bond => bond.Record), restored.Bonds.Select(bond => bond.Record));
        Assert.Equal(document.Config.LizardMap![0].OutputData, restored.Config.LizardMap![0].OutputData);
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
}
