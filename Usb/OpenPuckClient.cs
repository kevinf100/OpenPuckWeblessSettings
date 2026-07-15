using System.Buffers.Binary;

namespace OpenPuckWeblessSettings.Usb;

public sealed class OpenPuckClient(OpenPuckSession session)
{
    public OpenPuckSession Session => session;

    public async Task<PuckStatusSnapshot> RefreshPuckAsync(CancellationToken cancellationToken)
    {
        var frame = await session.ExchangeAsync([0x01], 0xA5, 12, TimeSpan.FromSeconds(2), cancellationToken);
        return OpenPuckStatusParser.ParsePuck(frame.Payload);
    }

    public async Task<PuckStatusSnapshot> SetFieldAsync(byte field, byte value, CancellationToken cancellationToken)
    {
        var frame = await session.ExchangeAsync([0x02, field, value], 0xA5, 12, TimeSpan.FromSeconds(2), cancellationToken);
        return OpenPuckStatusParser.ParsePuck(frame.Payload);
    }

    public async Task<IReadOnlyList<byte>> ApplySettingsAsync(
        OpenPuckSettingsDraft original,
        OpenPuckSettingsDraft draft,
        IProgress<(int Completed, int Total, byte Field)>? progress,
        CancellationToken cancellationToken)
    {
        var before = original.ToFieldMap();
        var dirty = draft.ToFieldMap().Where(pair => !before.TryGetValue(pair.Key, out var value) || value != pair.Value).ToArray();
        var accepted = new List<byte>();
        for (var index = 0; index < dirty.Length; index++)
        {
            await SetFieldAsync(dirty[index].Key, dirty[index].Value, cancellationToken);
            accepted.Add(dirty[index].Key);
            progress?.Report((index + 1, dirty.Length, dirty[index].Key));
        }
        return accepted;
    }

    public Task SwitchModeAsync(byte mode, CancellationToken cancellationToken) => SendImmediateAsync([0x03, mode], cancellationToken);
    public Task StartCaptureAsync(CancellationToken cancellationToken) => SendImmediateAsync([0x05, 1], cancellationToken);
    public Task StopCaptureAsync(CancellationToken cancellationToken) => SendImmediateAsync([0x05, 0], cancellationToken);
    public Task ClearHapticsAsync(CancellationToken cancellationToken) => SendImmediateAsync([0x07], cancellationToken);
    public Task PowerOffControllerAsync(CancellationToken cancellationToken) => SendImmediateAsync([0x08], cancellationToken);
    public Task RebootDebugCdcAsync(CancellationToken cancellationToken) => SendImmediateAsync([0x02, 20, 1], cancellationToken);
    public Task RebootSerialDfuAsync(CancellationToken cancellationToken) => SendImmediateAsync([0x0B], cancellationToken);
    public Task RebootUf2DfuAsync(CancellationToken cancellationToken) => SendImmediateAsync([0x0C], cancellationToken);
    public Task SetStabilityTestAsync(bool enabled, CancellationToken cancellationToken) => SendImmediateAsync([0x0F, enabled ? (byte)1 : (byte)0], cancellationToken);
    public Task FactoryEraseAsync(CancellationToken cancellationToken) => SendImmediateAsync([0x0A, (byte)'E', (byte)'R', (byte)'S'], cancellationToken);
    public Task WipeBoardAsync(CancellationToken cancellationToken) => SendImmediateAsync([0x25, (byte)'W', (byte)'I', (byte)'P', (byte)'E'], cancellationToken);
    public Task RemoveReversePuckBondAsync(byte slot, CancellationToken cancellationToken) => SendImmediateAsync([0x30, slot], cancellationToken);

    public async Task<byte[]> ExportBondsAsync(CancellationToken cancellationToken)
    {
        var frame = await session.ExchangeAsync([0x09], 0xA7, 98, TimeSpan.FromSeconds(3), cancellationToken);
        return frame.Payload;
    }

    public async Task<IReadOnlyList<LizardBinding>> LoadLizardMapAsync(CancellationToken cancellationToken)
    {
        return await session.ExclusiveAsync(async token =>
        {
            await session.WriteRawAsync([0x11], token);
            return await ReadLizardMapAsync(token);
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<LizardBinding>> SaveLizardMapAsync(
        IEnumerable<LizardBinding> bindings,
        CancellationToken cancellationToken)
    {
        var valid = bindings.Take(32).Where(binding =>
            binding.OutputType != 0 &&
            (binding.OutputType is not (1 or 2 or 5) || binding.TriggerMask != 0 || binding.HoldMask != 0)).ToArray();
        return await session.ExclusiveAsync(async token =>
        {
            await session.WriteRawAsync([(byte)0x13, (byte)valid.Length], token);
            for (var index = 0; index < valid.Length; index++)
            {
                await session.WriteRawAsync(EncodeLizardBinding(index, valid[index]), token);
            }
            await session.WriteRawAsync([0x14], token);
            return await ReadLizardMapAsync(token);
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<LizardBinding>> ResetLizardMapAsync(CancellationToken cancellationToken)
    {
        return await session.ExclusiveAsync(async token =>
        {
            await session.WriteRawAsync([0x15], token);
            return await ReadLizardMapAsync(token);
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<OpenPuckFrame>> LoadFlightRecorderAsync(CancellationToken cancellationToken)
    {
        return await session.ExclusiveAsync(async token =>
        {
            var frames = new List<OpenPuckFrame>();
            var restart = true;
            for (var batch = 0; batch < 128; batch++)
            {
                await session.WriteRawAsync([0x10, restart ? (byte)1 : (byte)0], token);
                restart = false;
                var bytes = await session.ReadRawAsync(256, TimeSpan.FromSeconds(2), token);
                var parsed = OpenPuckFrameCodec.Parse(bytes).Where(frame => frame.Marker == 0xA8).ToArray();
                frames.AddRange(parsed);
                if (parsed.Any(frame => frame.Payload.Length > 0 && frame.Payload[0] == 0)) break;
            }
            return frames;
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<OpenPuckFrame>> DrainCaptureAsync(CancellationToken cancellationToken)
    {
        return await session.ExclusiveAsync(async token =>
        {
            await session.WriteRawAsync([0x06], token);
            var frames = new List<OpenPuckFrame>();
            for (var guard = 0; guard < 400; guard++)
            {
                var bytes = await session.ReadRawAsync(256, TimeSpan.FromSeconds(2), token);
                var batch = OpenPuckFrameCodec.Parse(bytes).Where(frame => frame.Marker == 0xA6).ToArray();
                frames.AddRange(batch);
                if (batch.Any(frame => frame.Payload.Length > 0 && frame.Payload[0] == 0)) break;
            }
            return frames;
        }, cancellationToken);
    }

    public Task SendImmediateAsync(byte[] command, CancellationToken cancellationToken) =>
        session.ExclusiveAsync(token => session.WriteRawAsync(command, token), cancellationToken);

    private async Task<IReadOnlyList<LizardBinding>> ReadLizardMapAsync(CancellationToken cancellationToken)
    {
        var accumulated = new List<byte>();
        for (var guard = 0; guard < 64; guard++)
        {
            accumulated.AddRange(await session.ReadRawAsync(256, TimeSpan.FromSeconds(2), cancellationToken));
            var marker = accumulated.IndexOf(0xAA);
            if (marker >= 0 && marker + 2 <= accumulated.Count)
            {
                var total = marker + 2 + accumulated[marker + 1] * 16;
                if (accumulated.Count >= total) return OpenPuckFrameCodec.ParseLizardMap(accumulated.ToArray());
            }
        }
        throw new TimeoutException("Timed out waiting for the complete lizard-map reply.");
    }

    private static byte[] EncodeLizardBinding(int index, LizardBinding binding)
    {
        var command = new byte[18];
        command[0] = 0x12;
        command[1] = (byte)index;
        command[2] = binding.OutputType;
        binding.OutputData.AsSpan(0, Math.Min(7, binding.OutputData.Length)).CopyTo(command.AsSpan(3, 7));
        BinaryPrimitives.WriteUInt32LittleEndian(command.AsSpan(10), binding.TriggerMask);
        BinaryPrimitives.WriteUInt32LittleEndian(command.AsSpan(14), binding.HoldMask);
        return command;
    }
}
