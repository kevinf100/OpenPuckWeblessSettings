using LibUsbDotNet;
using LibUsbDotNet.LibUsb;
using LibUsbDotNet.Main;

namespace OpenPuckWeblessSettings.Usb;

public sealed class LibUsbOpenPuckTransport : IOpenPuckUsbTransport
{
    private const int TransferTimeoutMilliseconds = 2_000;

    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private UsbContext? _context;
    private UsbDeviceCollection? _connectedCollection;
    private UsbDevice? _connectedDevice;
    private UsbEndpointReader? _reader;
    private UsbEndpointWriter? _writer;
    private int? _claimedInterface;
    private bool _disposed;

    public bool IsConnected => _connectedDevice?.IsOpen == true && _reader is not null && _writer is not null;

    public async Task<IReadOnlyList<OpenPuckDevice>> ScanAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            return await Task.Run(() => ScanCore(cancellationToken), cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not OpenPuckUsbException)
        {
            throw WrapUsbFailure("USB scan failed", exception);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task ConnectAsync(OpenPuckDevice device, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);
        ThrowIfDisposed();
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            await Task.Run(() => ConnectCore(device, cancellationToken), cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not OpenPuckUsbException)
        {
            DisconnectCore();
            throw WrapUsbFailure("Could not connect to OpenPuck", exception);
        }
        finally
        {
            _operationGate.Release();
        }
    }


    public async Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            var copy = data.ToArray();
            await Task.Run(() => WriteCore(copy, cancellationToken), cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not OpenPuckUsbException)
        {
            throw WrapUsbFailure("USB write failed", exception);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<byte[]> ReadAsync(
        int maximumLength,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumLength);
        ThrowIfDisposed();
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            return await Task.Run(
                () => ReadCore(maximumLength, timeout, cancellationToken),
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not TimeoutException and not OpenPuckUsbException)
        {
            throw WrapUsbFailure("USB read failed", exception);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task DisconnectAsync()
    {
        await _operationGate.WaitAsync();
        try
        {
            await Task.Run(DisconnectCore);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await DisconnectAsync();
        _context?.Dispose();
        _operationGate.Dispose();
        _disposed = true;
    }

    private IReadOnlyList<OpenPuckDevice> ScanCore(CancellationToken cancellationToken)
    {
        var context = GetContext();
        using var devices = context.List();
        var results = new List<OpenPuckDevice>();
        var accessFailures = new List<string>();

        foreach (var device in devices.OfType<UsbDevice>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!UsbDescriptorSelector.SupportedVendorIds.Contains((ushort)device.VendorId))
            {
                continue;
            }

            try
            {
                device.Open();
                var selectedInterface = SelectInterface(device);
                if (selectedInterface is null)
                {
                    continue;
                }

                results.Add(ToOpenPuckDevice(device, selectedInterface));
            }
            catch (Exception exception)
            {
                accessFailures.Add($"{device.VendorId:X4}:{device.ProductId:X4}: {exception.Message}");
            }
            finally
            {
                if (device.IsOpen)
                {
                    device.Close();
                }
            }
        }

        if (results.Count == 0 && accessFailures.Count > 0)
        {
            throw new OpenPuckUsbException(
                "A possible OpenPuck USB device was found but could not be inspected. " +
                PlatformAccessAdvice() + " Details: " + string.Join("; ", accessFailures));
        }

        return results;
    }

    private void ConnectCore(OpenPuckDevice requestedDevice, CancellationToken cancellationToken)
    {
        DisconnectCore();

        try
        {
            var context = GetContext();
            _connectedCollection = context.List();
            _connectedDevice = FindRequestedDevice(_connectedCollection.OfType<UsbDevice>(), requestedDevice)
                ?? throw new OpenPuckUsbException("The selected OpenPuck is no longer connected. Rescan and try again.");

            _connectedDevice.Open();
            UsbDevice.ControlTransferTimeout = TransferTimeoutMilliseconds;
            _connectedDevice.SetAutoDetachKernelDriver(true);

            if (_connectedDevice.Configuration == 0)
            {
                _connectedDevice.SetConfiguration(1);
            }

            var selectedInterface = SelectInterface(_connectedDevice)
                ?? throw new OpenPuckUsbException(
                    "The selected device no longer exposes the OpenPuck vendor bulk interface.");

            if (!_connectedDevice.ClaimInterface(selectedInterface.Number))
            {
                throw new OpenPuckUsbException(
                    $"USB interface {selectedInterface.Number} could not be claimed. {PlatformAccessAdvice()}");
            }

            _claimedInterface = selectedInterface.Number;
            var setup = new UsbSetupPacket(0x21, 0x22, 0x01, selectedInterface.Number, 0);
            _connectedDevice.ControlTransfer(setup);

            _writer = _connectedDevice.OpenEndpointWriter((WriteEndpointID)selectedInterface.BulkOutEndpoint);
            _reader = _connectedDevice.OpenEndpointReader((ReadEndpointID)selectedInterface.BulkInEndpoint);

            cancellationToken.ThrowIfCancellationRequested();
        }
        catch
        {
            DisconnectCore();
            throw;
        }
    }


    private void WriteCore(byte[] data, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var writer = _writer ?? throw new OpenPuckUsbException("No OpenPuck target is connected.");
        var result = writer.Write(data, TransferTimeoutMilliseconds, out var bytesWritten);
        if ((int)result != 0 || bytesWritten != data.Length)
        {
            throw new IOException($"Bulk OUT failed ({result}, {bytesWritten}/{data.Length} bytes).");
        }
    }

    private byte[] ReadCore(int maximumLength, TimeSpan timeout, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var reader = _reader ?? throw new OpenPuckUsbException("No OpenPuck target is connected.");
        var buffer = new byte[maximumLength];
        var timeoutMs = (int)Math.Clamp(timeout.TotalMilliseconds, 1, int.MaxValue);
        var result = reader.Read(buffer, timeoutMs, out var bytesRead);
        cancellationToken.ThrowIfCancellationRequested();
        if ((int)result != 0)
        {
            if (result.ToString().Contains("Timeout", StringComparison.OrdinalIgnoreCase))
            {
                throw new TimeoutException($"Bulk IN timed out after {timeoutMs} ms.");
            }

            throw new IOException($"Bulk IN failed ({result}).");
        }

        return buffer.AsSpan(0, bytesRead).ToArray();
    }

    private static OpenPuckInterface? SelectInterface(UsbDevice device)
    {
        var interfaces = device.Configs
            .SelectMany(configuration => configuration.Interfaces)
            .Select(candidate => new UsbInterfaceDescriptor(
                (byte)candidate.Number,
                (byte)candidate.Class,
                (byte)candidate.SubClass,
                candidate.Endpoints
                    .Select(endpoint => new UsbEndpointDescriptor(
                        (byte)endpoint.EndpointAddress,
                        (byte)endpoint.Attributes))
                    .ToArray()))
            .ToArray();

        return UsbDescriptorSelector.FindOpenPuckInterface((ushort)device.VendorId, interfaces);
    }

    private static OpenPuckDevice ToOpenPuckDevice(UsbDevice device, OpenPuckInterface selectedInterface)
    {
        string product;
        string serial;
        try
        {
            product = device.Info.Product ?? string.Empty;
            serial = device.Info.SerialNumber ?? string.Empty;
        }
        catch
        {
            product = string.Empty;
            serial = string.Empty;
        }

        return new OpenPuckDevice(
            DeviceKey(device),
            (ushort)device.VendorId,
            (ushort)device.ProductId,
            product,
            serial,
            selectedInterface.Number,
            selectedInterface.BulkInEndpoint,
            selectedInterface.BulkOutEndpoint);
    }

    private static UsbDevice? FindRequestedDevice(
        IEnumerable<UsbDevice> devices,
        OpenPuckDevice requestedDevice)
    {
        var vendorMatches = devices
            .Where(device => device.VendorId == requestedDevice.VendorId && device.ProductId == requestedDevice.ProductId)
            .ToArray();

        return vendorMatches.FirstOrDefault(device => DeviceKey(device) == requestedDevice.DeviceKey)
            ?? (vendorMatches.Length == 1 ? vendorMatches[0] : null);
    }

    private static string DeviceKey(UsbDevice device) => $"{device.BusNumber}:{device.Address}";

    private UsbContext GetContext()
    {
        try
        {
            return _context ??= new UsbContext();
        }
        catch (DllNotFoundException exception)
        {
            throw new OpenPuckUsbException(
                "The native libusb-1.0 library was not found. Install or package libusb for this operating system and architecture.",
                exception);
        }
    }

    private void DisconnectCore()
    {
        try
        {
            if (_connectedDevice is not null && _claimedInterface is int interfaceNumber)
            {
                _connectedDevice.ReleaseInterface(interfaceNumber);
            }
        }
        catch
        {
            // The device may already have been unplugged. Cleanup must remain best-effort.
        }
        finally
        {
            _reader = null;
            _writer = null;
            _claimedInterface = null;

            try
            {
                _connectedDevice?.Close();
            }
            catch
            {
                // Ignore close failures from an unplugged device.
            }

            _connectedDevice = null;
            _connectedCollection?.Dispose();
            _connectedCollection = null;
        }
    }

    private static OpenPuckUsbException WrapUsbFailure(string operation, Exception exception) =>
        new($"{operation}. {PlatformAccessAdvice()} Details: {exception.Message}", exception);

    private static string PlatformAccessAdvice()
    {
        if (OperatingSystem.IsLinux())
        {
            return "Check the OpenPuck udev rule and plugdev membership, then replug the device.";
        }

        if (OperatingSystem.IsWindows())
        {
            return "Close Chrome, Edge, Steam, or another tool that may own the WinUSB interface, and verify the WinUSB driver is active.";
        }

        if (OperatingSystem.IsMacOS())
        {
            return "Close any process holding the USB interface and verify the packaged libusb library matches this Mac architecture.";
        }

        return "Verify USB permissions, the libusb installation, and that no other process owns the interface.";
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
