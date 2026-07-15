using System.Buffers.Binary;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using OpenPuckWeblessSettings.Usb;

namespace OpenPuckWeblessSettings.Services;

public static class Uf2FirmwareParser
{
    private const uint Magic0 = 0x0A324655;
    private const uint Magic1 = 0x9E5D5157;
    private const uint MagicEnd = 0x0AB16F30;
    private const uint Nrf52840Family = 0xADA52840;
    private const uint AppBase = 0x26000;
    private const int MaximumImageLength = 0x60000;

    public static FirmwareImage Parse(string name, ReadOnlySpan<byte> uf2)
    {
        if (uf2.Length == 0 || uf2.Length % 512 != 0) throw new InvalidDataException("Not a UF2: size is not a multiple of 512 bytes.");
        var blocks = new List<(uint Address, byte[] Payload)>();
        var low = uint.MaxValue;
        uint high = 0;
        for (var offset = 0; offset < uf2.Length; offset += 512)
        {
            var block = uf2.Slice(offset, 512);
            if (U32(block, 0) != Magic0 || U32(block, 4) != Magic1 || U32(block, 508) != MagicEnd)
                throw new InvalidDataException($"Bad UF2 block magic at offset {offset}.");
            var flags = U32(block, 8);
            if ((flags & 1) != 0) continue;
            var address = U32(block, 12);
            var length = checked((int)U32(block, 16));
            if ((flags & 0x2000) != 0 && U32(block, 28) != Nrf52840Family)
                throw new InvalidDataException("UF2 family is not nRF52840 (0xADA52840).");
            if (length is < 1 or > 476) throw new InvalidDataException($"Bad UF2 payload size {length}.");
            var payload = block.Slice(32, length).ToArray();
            blocks.Add((address, payload));
            low = Math.Min(low, address);
            high = Math.Max(high, checked(address + (uint)length));
        }
        if (blocks.Count == 0) throw new InvalidDataException("UF2 contains no main-flash data.");
        if (low != AppBase) throw new InvalidDataException($"Image base 0x{low:X} is not the OpenPuck app region 0x{AppBase:X}.");
        if (high - low > MaximumImageLength) throw new InvalidDataException("Image exceeds the 384 KiB staged-update limit.");
        var lengthPadded = checked((int)((high - low + 3) & ~3u));
        var image = Enumerable.Repeat((byte)0xFF, lengthPadded).ToArray();
        foreach (var block in blocks) block.Payload.CopyTo(image, checked((int)(block.Address - low)));
        return new FirmwareImage(name, image, Crc32(image), Encoding.Latin1.GetString(image).Contains("OPK-FWUP-v1", StringComparison.Ordinal));
    }

    public static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ (0xEDB88320u & (uint)-(int)(crc & 1));
        }
        return ~crc;
    }

    private static uint U32(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
}

public sealed class FirmwareUpdateService(OpenPuckSession session)
{
    private static readonly string[] Errors =
    [
        "ok", "command out of sequence", "image too big for spare flash", "offset resync",
        "staged bytes failed CRC verify", "staged image has no valid vector table"
    ];

    public async Task UpdateAsync(
        FirmwareImage image,
        IProgress<FirmwareUpdateProgress>? progress,
        CancellationToken cancellationToken)
    {
        await session.ExclusiveAsync(async token =>
        {
            progress?.Report(new("Preparing", null, 0, image.Data.Length));
            var begin = new byte[9];
            begin[0] = 0x20;
            BinaryPrimitives.WriteUInt32LittleEndian(begin.AsSpan(1), (uint)image.Data.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(begin.AsSpan(5), image.Crc32);
            var acknowledgement = await ControlAsync(begin, TimeSpan.FromSeconds(4), "begin", token);
            ThrowForStatus(acknowledgement, "begin");

            uint offset = 0;
            var sends = 0;
            var maximumSends = (image.Data.Length + 127) / 128 * 2 + 64;
            while (offset < image.Data.Length)
            {
                var length = Math.Min(128, image.Data.Length - checked((int)offset));
                var command = new byte[6 + length];
                command[0] = 0x21;
                BinaryPrimitives.WriteUInt32LittleEndian(command.AsSpan(1), offset);
                command[5] = (byte)length;
                image.Data.AsSpan(checked((int)offset), length).CopyTo(command.AsSpan(6));
                await session.WriteRawAsync(command, token);
                if (++sends > maximumSends) throw new IOException($"Firmware transfer did not converge at offset {offset}.");
                var progressed = false;
                for (var read = 0; read < 8; read++)
                {
                    FirmwareAcknowledgement? ack;
                    try { ack = await ReadAcknowledgementAsync(TimeSpan.FromSeconds(2.5), token); }
                    catch (TimeoutException) { break; }
                    if (ack.Status == 3) { offset = ack.NextOffset; progressed = true; break; }
                    ThrowForStatus(ack, $"chunk at offset {offset}");
                    if (ack.NextOffset > offset) { offset = ack.NextOffset; progressed = true; break; }
                }
                if (!progressed) continue;
                progress?.Report(new("Sending to OpenPuck", (int)(offset * 100 / (uint)image.Data.Length), offset, image.Data.Length));
            }

            progress?.Report(new("Verifying on OpenPuck", null, image.Data.Length, image.Data.Length));
            acknowledgement = await ControlAsync([0x22], TimeSpan.FromSeconds(8), "verify and commit", token);
            ThrowForStatus(acknowledgement, "verify and commit");
            progress?.Report(new("Rebooting", null, image.Data.Length, image.Data.Length));
            try { await session.WriteRawAsync([0x23], token); } catch { /* Expected disconnect. */ }
        }, cancellationToken);
    }

    public Task AbortAsync(CancellationToken cancellationToken) =>
        session.ExclusiveAsync(token => session.WriteRawAsync([0x24], token), cancellationToken);

    private async Task<FirmwareAcknowledgement> ControlAsync(byte[] command, TimeSpan timeout, string label, CancellationToken token)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await session.WriteRawAsync(command, token);
            try { return await ReadAcknowledgementAsync(timeout, token); }
            catch (TimeoutException) when (attempt < 2) { }
        }
        throw new TimeoutException($"No firmware acknowledgement for {label} after three attempts.");
    }

    private async Task<FirmwareAcknowledgement> ReadAcknowledgementAsync(TimeSpan timeout, CancellationToken token)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var remaining = deadline - DateTimeOffset.UtcNow;
            byte[] bytes;
            try { bytes = await session.ReadRawAsync(256, remaining, token); }
            catch (TimeoutException) { break; }
            try
            {
                // A retried command can produce two acknowledgements in one USB read. The newest (last)
                // firmware state is authoritative; stale status/wedge frames in the same read are ignored.
                var frame = OpenPuckFrameCodec.FindLast(bytes, 0xAB, 5);
                return new FirmwareAcknowledgement(frame.Payload[0], BinaryPrimitives.ReadUInt32LittleEndian(frame.Payload.AsSpan(1)));
            }
            catch (InvalidDataException) { }
        }
        throw new TimeoutException("Timed out waiting for a firmware acknowledgement.");
    }

    private static void ThrowForStatus(FirmwareAcknowledgement acknowledgement, string operation)
    {
        if (acknowledgement.Status == 0) return;
        var message = acknowledgement.Status < Errors.Length ? Errors[acknowledgement.Status] : $"code {acknowledgement.Status}";
        throw new IOException($"Firmware {operation} rejected: {message} (next offset {acknowledgement.NextOffset}).");
    }
}

public sealed class GitHubFirmwareReleaseService(HttpClient? httpClient = null) : IDisposable
{
    private readonly HttpClient _http = httpClient ?? new HttpClient();
    private IReadOnlyList<FirmwareRelease>? _cache;
    private readonly bool _ownsClient = httpClient is null;

    public async Task<IReadOnlyList<FirmwareRelease>> GetReleasesAsync(bool includePrereleases, CancellationToken cancellationToken)
    {
        if (_cache is null)
        {
            _http.DefaultRequestHeaders.UserAgent.Clear();
            _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("OpenPuckNative", "1.0"));
            using var response = await _http.GetAsync("https://api.github.com/repos/safijari/openpuck/releases", cancellationToken);
            if ((int)response.StatusCode is 403 or 429)
                throw new HttpRequestException("GitHub release discovery is rate-limited. Local UF2 updating remains available.");
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            _cache = document.RootElement.EnumerateArray().Select(ParseRelease).ToArray();
        }
        return _cache.Where(release => includePrereleases || !release.IsPrerelease).ToArray();
    }

    public async Task<byte[]> DownloadAsync(Uri uri, CancellationToken cancellationToken) =>
        await _http.GetByteArrayAsync(uri, cancellationToken);

    private static FirmwareRelease ParseRelease(JsonElement release)
    {
        Uri? standard = null, factory = null;
        foreach (var asset in release.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString() ?? "";
            if (!name.EndsWith(".uf2", StringComparison.OrdinalIgnoreCase)) continue;
            var uri = new Uri(asset.GetProperty("browser_download_url").GetString()!);
            if (name.Contains("factory-reset", StringComparison.OrdinalIgnoreCase)) factory = uri;
            else standard ??= uri;
        }
        return new FirmwareRelease(
            release.GetProperty("tag_name").GetString() ?? "?",
            release.GetProperty("name").GetString() ?? "OpenPuck",
            release.GetProperty("prerelease").GetBoolean(),
            release.GetProperty("published_at").GetDateTimeOffset(), standard, factory);
    }

    public void Dispose() { if (_ownsClient) _http.Dispose(); }
}
