namespace StrafeLab.Input;

public sealed class MchoseLightingProtocol
{
    public bool IsCommunityVerifiedFor(int vendorId, int productId)
        => (vendorId, productId) is (0x41E4, 0x2116) or (0x41E4, 0x2132) or (0x3837, 0x3003);

    /// <summary>
    /// Community SignalRGB adapters document this as a 65-byte output report:
    /// 54 RGB payload bytes per packet, five packets for the 68-key matrix. The
    /// Air-II PID 0x2120 is deliberately excluded from the verified list.
    /// </summary>
    public IReadOnlyList<byte[]> BuildVerifiedCandidateFrame(int vendorId, int productId, IReadOnlyList<byte> rgb)
    {
        if (!IsCommunityVerifiedFor(vendorId, productId))
        {
            throw new InvalidOperationException("该 VID/PID 的灯光协议尚未验证，已拒绝写入。");
        }

        var buffer = new byte[270];
        for (var i = 0; i < Math.Min(buffer.Length, rgb.Count); i++) buffer[i] = rgb[i];
        var packets = new List<byte[]>();
        for (var packetIndex = 0; packetIndex < 5; packetIndex++)
        {
            var packet = new byte[65];
            var offset = packetIndex * 54;
            packet[1] = 0x55;
            packet[2] = 0xDD;
            packet[5] = 0x36;
            packet[6] = (byte)(offset & 0xFF);
            packet[7] = (byte)((offset >> 8) & 0xFF);
            for (var i = 0; i < 54; i++) packet[9 + i] = buffer[offset + i];
            var checksum = 0;
            for (var i = 5; i < packet.Length; i++) checksum += packet[i];
            packet[4] = (byte)(checksum & 0xFF);
            packets.Add(packet);
        }

        return packets;
    }
}
