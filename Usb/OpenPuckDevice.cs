namespace OpenPuckWeblessSettings.Usb;

public sealed record OpenPuckDevice(
    string DeviceKey,
    ushort VendorId,
    ushort ProductId,
    string Product,
    string SerialNumber,
    byte InterfaceNumber,
    byte BulkInEndpoint,
    byte BulkOutEndpoint)
{
    public string DisplayName =>
        $"{(string.IsNullOrWhiteSpace(Product) ? "OpenPuck target" : Product)}  " +
        $"{VendorId:X4}:{ProductId:X4}" +
        (string.IsNullOrWhiteSpace(SerialNumber) ? string.Empty : $"  ·  {SerialNumber}");

    public string EndpointSummary =>
        $"Interface {InterfaceNumber} · IN 0x{BulkInEndpoint:X2} · OUT 0x{BulkOutEndpoint:X2}";

    public override string ToString() => DisplayName;
}
