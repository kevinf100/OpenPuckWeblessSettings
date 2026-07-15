using OpenPuckWeblessSettings.Usb;

namespace OpenPuckWeblessSettings;

internal static class HardwareProbe
{
    public static async Task<int> RunAsync()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var transport = new LibUsbOpenPuckTransport();
        await using var session = new OpenPuckSession(transport);

        try
        {
            var targets = await transport.ScanAsync(cancellation.Token);
            Console.WriteLine($"OpenPuck microcontroller targets found: {targets.Count}");

            foreach (var target in targets)
            {
                Console.WriteLine(
                    $"- {target.DisplayName} | {target.EndpointSummary} | key {target.DeviceKey}");

                try
                {
                    var profile = await session.ConnectAsync(target, cancellation.Token);
                    var linked = profile.Kind == OpenPuckTargetKind.ReversePuck
                        ? session.ReversePuckStatus?.RadioLinked == true
                        : session.PuckStatus?.RadioPeerLinked == true;
                    Console.WriteLine(
                        $"  CONNECTED: {profile.Kind}, protocol v{profile.ProtocolVersion}, " +
                        $"downstream RF peer {(linked ? "linked" : "not linked")}");
                }
                catch (Exception exception)
                {
                    Console.WriteLine($"  CONNECTION FAILED: {FlattenException(exception)}");
                }
                finally
                {
                    await session.DisconnectAsync();
                }
            }

            return targets.Count > 0 ? 0 : 1;
        }
        catch (Exception exception)
        {
            Console.WriteLine($"OpenPuck scan failed: {FlattenException(exception)}");
            return 2;
        }
    }

    private static string FlattenException(Exception exception)
    {
        var messages = new List<string>();
        for (var current = exception; current is not null; current = current.InnerException!)
        {
            if (!messages.Contains(current.Message, StringComparer.Ordinal))
            {
                messages.Add(current.Message);
            }
        }

        return string.Join(" ", messages);
    }
}
