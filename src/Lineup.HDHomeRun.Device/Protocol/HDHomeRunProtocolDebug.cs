using System.Text;

namespace Lineup.HDHomeRun.Device.Protocol;

/// <summary>
/// Debug utilities for comparing HDHomeRun protocol packets.
/// Use with Wireshark captures from hdhomerun_config to identify differences.
/// </summary>
public static class HDHomeRunProtocolDebug
{
    /// <summary>
    /// Builds a GET request packet and returns detailed debug info.
    /// Compare this output with Wireshark capture of: hdhomerun_config &lt;device_id&gt; get /sys/model
    /// </summary>
    public static PacketDebugInfo BuildGetPacket(string variableName)
    {
        var builder = new HDHomeRunPacketBuilder();

        // Add the variable name with null terminator (exactly as libhdhomerun does)
        var nameWithNull = variableName + "\0";
        var nameBytes = Encoding.UTF8.GetBytes(nameWithNull);

        builder.AddTag(HDHomeRunTagType.GetSetName, nameBytes);
        var packet = builder.Build(HDHomeRunPacketType.GetSetRequest);

        return new PacketDebugInfo
        {
            VariableName = variableName,
            NameBytes = nameBytes,
            FullPacket = packet,
            Header = packet[..4],
            Payload = packet[4..^4],
            Crc = packet[^4..],
            PacketType = (ushort)((packet[0] << 8) | packet[1]),
            PayloadLength = (ushort)((packet[2] << 8) | packet[3]),
            CrcValue = BitConverter.ToUInt32(packet, packet.Length - 4),
            CalculatedCrc = HDHomeRunPacketBuilder.CalculateCrc32(packet.AsSpan(0, packet.Length - 4))
        };
    }

    /// <summary>
    /// Compares our packet with a Wireshark capture (hex string).
    /// </summary>
    /// <param name="variableName">Variable name (e.g., "/sys/model")</param>
    /// <param name="wiresharkHex">Hex string from Wireshark (e.g., "00 04 00 0D ...")</param>
    /// <returns>Comparison result</returns>
    public static PacketComparisonResult CompareWithCapture(string variableName, string wiresharkHex)
    {
        var ourPacket = BuildGetPacket(variableName);
        var capturedBytes = ParseHexString(wiresharkHex);

        var result = new PacketComparisonResult
        {
            OurPacket = ourPacket.FullPacket,
            CapturedPacket = capturedBytes,
            Differences = []
        };

        if (capturedBytes.Length != ourPacket.FullPacket.Length)
        {
            result.Differences.Add($"Length mismatch: ours={ourPacket.FullPacket.Length}, captured={capturedBytes.Length}");
        }

        var minLen = Math.Min(capturedBytes.Length, ourPacket.FullPacket.Length);
        for (int i = 0; i < minLen; i++)
        {
            if (ourPacket.FullPacket[i] != capturedBytes[i])
            {
                result.Differences.Add($"Byte {i}: ours=0x{ourPacket.FullPacket[i]:X2}, captured=0x{capturedBytes[i]:X2}");
            }
        }

        result.IsMatch = result.Differences.Count == 0;
        return result;
    }

    /// <summary>
    /// Parses a hex string (with or without spaces/dashes) to bytes.
    /// </summary>
    public static byte[] ParseHexString(string hex)
    {
        // Remove common separators
        hex = hex.Replace(" ", "").Replace("-", "").Replace(":", "").Replace("\n", "").Replace("\r", "");

        if (hex.Length % 2 != 0)
        {
            throw new ArgumentException("Hex string must have even length");
        }

        var bytes = new byte[hex.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        }
        return bytes;
    }

    /// <summary>
    /// Formats bytes as a hex dump string.
    /// </summary>
    public static string FormatHexDump(byte[] data)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < data.Length; i += 16)
        {
            sb.Append($"{i:X4}:  ");

            // Hex bytes
            for (int j = 0; j < 16; j++)
            {
                if (i + j < data.Length)
                    sb.Append($"{data[i + j]:X2} ");
                else
                    sb.Append("   ");

                if (j == 7) sb.Append(' ');
            }

            sb.Append(" |");

            // ASCII
            for (int j = 0; j < 16 && i + j < data.Length; j++)
            {
                var c = (char)data[i + j];
                sb.Append(c is >= ' ' and <= '~' ? c : '.');
            }

            sb.AppendLine("|");
        }
        return sb.ToString();
    }

    /// <summary>
    /// Calculates and verifies CRC for given packet data.
    /// </summary>
    public static CrcVerificationResult VerifyCrc(byte[] packetWithCrc)
    {
        if (packetWithCrc.Length < 8)
        {
            return new CrcVerificationResult { IsValid = false, Error = "Packet too short" };
        }

        var dataLength = packetWithCrc.Length - 4;
        var storedCrc = BitConverter.ToUInt32(packetWithCrc, dataLength);
        var calculatedCrc = HDHomeRunPacketBuilder.CalculateCrc32(packetWithCrc.AsSpan(0, dataLength));

        return new CrcVerificationResult
        {
            IsValid = storedCrc == calculatedCrc,
            StoredCrc = storedCrc,
            CalculatedCrc = calculatedCrc,
            Error = storedCrc != calculatedCrc ? $"CRC mismatch: stored=0x{storedCrc:X8}, calculated=0x{calculatedCrc:X8}" : null
        };
    }

    /// <summary>
    /// Generates test vectors that should match libhdhomerun output.
    /// Run hdhomerun_config with Wireshark to capture the actual bytes and compare.
    /// </summary>
    public static string GenerateTestVectors()
    {
        var sb = new StringBuilder();
        sb.AppendLine("HDHomeRun Protocol Test Vectors");
        sb.AppendLine("================================");
        sb.AppendLine();
        sb.AppendLine("To verify our implementation matches libhdhomerun:");
        sb.AppendLine("1. Start Wireshark capturing on your network interface");
        sb.AppendLine("2. Filter: tcp.port == 65001");
        sb.AppendLine("3. Run: hdhomerun_config <device_id> get /sys/model");
        sb.AppendLine("4. Find the TCP packet sent to the device");
        sb.AppendLine("5. Copy the TCP payload bytes and compare with below");
        sb.AppendLine();

        var testCases = new[] { "/sys/model", "/sys/version", "/tuner0/status" };

        foreach (var name in testCases)
        {
            var debug = BuildGetPacket(name);
            sb.AppendLine($"GET {name}");
            sb.AppendLine($"  Command: hdhomerun_config <device_id> get {name}");
            sb.AppendLine($"  Expected packet ({debug.FullPacket.Length} bytes):");
            sb.AppendLine($"  Hex: {BitConverter.ToString(debug.FullPacket).Replace("-", " ")}");
            sb.AppendLine($"  ");
            sb.AppendLine($"  Header:  Type=0x{debug.PacketType:X4}, Length=0x{debug.PayloadLength:X4}");
            sb.AppendLine($"  Payload: Tag=0x03, VarLen={debug.NameBytes.Length}, Name=\"{name}\\0\"");
            sb.AppendLine($"  CRC32:   0x{debug.CrcValue:X8} (little-endian bytes: {BitConverter.ToString(debug.Crc).Replace("-", " ")})");
            sb.AppendLine($"  CRC OK:  {debug.CrcValue == debug.CalculatedCrc}");
            sb.AppendLine();
        }

        return sb.ToString();
    }
}

public record PacketDebugInfo
{
    public required string VariableName { get; init; }
    public required byte[] NameBytes { get; init; }
    public required byte[] FullPacket { get; init; }
    public required byte[] Header { get; init; }
    public required byte[] Payload { get; init; }
    public required byte[] Crc { get; init; }
    public ushort PacketType { get; init; }
    public ushort PayloadLength { get; init; }
    public uint CrcValue { get; init; }
    public uint CalculatedCrc { get; init; }
}

public record PacketComparisonResult
{
    public required byte[] OurPacket { get; init; }
    public required byte[] CapturedPacket { get; init; }
    public required List<string> Differences { get; init; }
    public bool IsMatch { get; set; }
}

public record CrcVerificationResult
{
    public bool IsValid { get; init; }
    public uint StoredCrc { get; init; }
    public uint CalculatedCrc { get; init; }
    public string? Error { get; init; }
}
