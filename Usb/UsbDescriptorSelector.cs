namespace OpenPuckWeblessSettings.Usb;

public sealed record UsbEndpointDescriptor(byte Address, byte Attributes)
{
    public bool IsBulk => (Attributes & 0x03) == 0x02;
    public bool IsIn => (Address & 0x80) != 0;
}

public sealed record UsbInterfaceDescriptor(
    byte Number,
    byte Class,
    byte SubClass,
    IReadOnlyList<UsbEndpointDescriptor> Endpoints);

public sealed record OpenPuckInterface(byte Number, byte BulkInEndpoint, byte BulkOutEndpoint);

public static class UsbDescriptorSelector
{
    public static readonly IReadOnlySet<ushort> SupportedVendorIds = new HashSet<ushort>
    {
        0x28DE,
        0x045E,
        0x057E,
        0x0F0D,
        0x054C
    };

    public static OpenPuckInterface? FindOpenPuckInterface(
        ushort vendorId,
        IEnumerable<UsbInterfaceDescriptor> interfaces)
    {
        if (!SupportedVendorIds.Contains(vendorId))
        {
            return null;
        }

        foreach (var candidate in interfaces)
        {
            if (candidate.Class != 0xFF || candidate.SubClass == 0x5D)
            {
                continue;
            }

            var bulkIn = candidate.Endpoints.FirstOrDefault(endpoint => endpoint.IsBulk && endpoint.IsIn);
            var bulkOut = candidate.Endpoints.FirstOrDefault(endpoint => endpoint.IsBulk && !endpoint.IsIn);
            if (bulkIn is not null && bulkOut is not null)
            {
                return new OpenPuckInterface(candidate.Number, bulkIn.Address, bulkOut.Address);
            }
        }

        return null;
    }
}
