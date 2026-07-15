using OpenPuckWeblessSettings.Usb;

namespace OpenPuckWeblessSettings.Tests;

public sealed class UsbDescriptorSelectorTests
{
    [Fact]
    public void AcceptsVendorInterfaceWithBulkInAndOut()
    {
        var descriptor = Interface(4, 0xFF, 0x00,
            new(0x84, 0x02),
            new(0x04, 0x02));

        var result = UsbDescriptorSelector.FindOpenPuckInterface(0x28DE, [descriptor]);

        Assert.Equal(new OpenPuckInterface(4, 0x84, 0x04), result);
    }

    [Fact]
    public void RejectsUnsupportedVendor()
    {
        var descriptor = Interface(1, 0xFF, 0x00,
            new(0x81, 0x02),
            new(0x01, 0x02));

        Assert.Null(UsbDescriptorSelector.FindOpenPuckInterface(0x1234, [descriptor]));
    }

    [Fact]
    public void RejectsSteamHidSubclass()
    {
        var descriptor = Interface(1, 0xFF, 0x5D,
            new(0x81, 0x02),
            new(0x01, 0x02));

        Assert.Null(UsbDescriptorSelector.FindOpenPuckInterface(0x28DE, [descriptor]));
    }

    [Fact]
    public void RejectsInterfaceMissingBulkDirection()
    {
        var descriptor = Interface(1, 0xFF, 0x00,
            new(0x81, 0x02),
            new(0x82, 0x02));

        Assert.Null(UsbDescriptorSelector.FindOpenPuckInterface(0x28DE, [descriptor]));
    }

    [Fact]
    public void IgnoresInterruptEndpointsAndFindsLaterValidInterface()
    {
        var interruptOnly = Interface(1, 0xFF, 0x00,
            new(0x81, 0x03),
            new(0x01, 0x03));
        var valid = Interface(6, 0xFF, 0x01,
            new(0x86, 0x02),
            new(0x06, 0x02));

        var result = UsbDescriptorSelector.FindOpenPuckInterface(0x045E, [interruptOnly, valid]);

        Assert.Equal(new OpenPuckInterface(6, 0x86, 0x06), result);
    }

    private static UsbInterfaceDescriptor Interface(
        byte number,
        byte classCode,
        byte subClass,
        params UsbEndpointDescriptor[] endpoints) =>
        new(number, classCode, subClass, endpoints);
}
