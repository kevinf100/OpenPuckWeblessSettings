namespace OpenPuckWeblessSettings.Usb;

public sealed class OpenPuckUsbException(string message, Exception? innerException = null)
    : Exception(message, innerException);
