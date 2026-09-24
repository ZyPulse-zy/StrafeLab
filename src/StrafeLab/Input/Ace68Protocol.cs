using StrafeLab.Core;

namespace StrafeLab.Input;

public sealed record HallSample(long TimestampUs, InputControl Control, int SensorValue,
    int TravelUnits, double Millimeters, byte CalibrationStatus)
{
    public double Confidence => CalibrationStatus == 255 ? 0.9 : 0.3;
}

/// <summary>Protocol verified on 41E4:2120 firmware 0x0117. Offsets exclude report ID.</summary>
public static class Ace68Protocol
{
    public const ushort VerifiedFirmware = 0x0117;
    public static byte[] Command(byte command, int offset, int size, byte[]? data = null)
    {
        if (command is not (3 or 4 or 5 or 6)) throw new ArgumentOutOfRangeException(nameof(command));
        if (size is < 1 or > 56 || offset is < 0 or > 65535 || (data != null && data.Length != size))
            throw new ArgumentException("Invalid command range");
        if ((command == 6) != (data != null)) throw new ArgumentException("Only function config writes are supported");
        var packet = new byte[65]; // Windows/HidSharp include the zero report ID.
        packet[1] = 0x55; packet[2] = command; packet[5] = (byte)size;
        packet[6] = (byte)offset; packet[7] = (byte)(offset >> 8);
        data?.CopyTo(packet, 9);
        packet[4] = unchecked((byte)packet.Skip(5).Sum(x => x));
        return packet;
    }

    public static bool TryDecode(ReadOnlySpan<byte> payload, long timestampUs, out HallSample? sample)
    {
        sample = null;
        if (payload.Length == 65 && payload[0] == 0) payload = payload[1..];
        if (payload.Length != 64 || payload[0] != 0xA0 || payload[1] != 0x10 || payload[2] != 0) return false;
        InputControl? key = payload[3] switch { 4 => InputControl.A, 7 => InputControl.D, 26 => InputControl.W, 22 => InputControl.S, _ => null };
        if (key == null) return false;
        var travel = (payload[6] << 8) | payload[7];
        if (travel > 500) return false;
        sample = new(timestampUs, key.Value, (payload[4] << 8) | payload[5], travel, travel / 100d, payload[10]);
        return true;
    }
}
