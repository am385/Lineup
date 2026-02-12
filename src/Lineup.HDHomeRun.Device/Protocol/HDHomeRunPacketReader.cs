using System.Buffers.Binary;
using System.Text;

namespace Lineup.HDHomeRun.Device.Protocol;

/// <summary>
/// Parses HDHomeRun protocol packets and extracts TLV entries.
/// </summary>
public ref struct HDHomeRunPacketReader
{
    private ReadOnlySpan<byte> _data;
    private int _position;

    /// <summary>
    /// Gets the packet type
    /// </summary>
    public HDHomeRunPacketType PacketType { get; }

    /// <summary>
    /// Gets the payload length
    /// </summary>
    public int PayloadLength { get; }

    /// <summary>
    /// Gets whether the packet has valid CRC
    /// </summary>
    public bool IsValid { get; }

    /// <summary>
    /// Creates a new packet reader
    /// </summary>
    public HDHomeRunPacketReader(ReadOnlySpan<byte> packet)
    {
        _data = packet;
        _position = 0;

        if (packet.Length < HDHomeRunPacketBuilder.HeaderSize + HDHomeRunPacketBuilder.CrcSize)
        {
            PacketType = 0;
            PayloadLength = 0;
            IsValid = false;
            return;
        }

        // Verify CRC
        IsValid = HDHomeRunPacketBuilder.VerifyCrc32(packet);
        if (!IsValid)
        {
            PacketType = 0;
            PayloadLength = 0;
            return;
        }

        // Read header (big-endian)
        PacketType = (HDHomeRunPacketType)BinaryPrimitives.ReadUInt16BigEndian(packet);
        PayloadLength = BinaryPrimitives.ReadUInt16BigEndian(packet[2..]);

        // Skip header
        _position = HDHomeRunPacketBuilder.HeaderSize;
        _data = packet[..(HDHomeRunPacketBuilder.HeaderSize + PayloadLength)];
    }

    /// <summary>
    /// Tries to read the next TLV entry
    /// </summary>
    public bool TryReadTag(out HDHomeRunTagType tag, out ReadOnlySpan<byte> value)
    {
        tag = 0;
        value = default;

        if (_position >= _data.Length)
            return false;

        // Read tag type
        tag = (HDHomeRunTagType)_data[_position++];

        // Read length (variable-length encoding)
        if (_position >= _data.Length)
            return false;

        int length = _data[_position++];
        if ((length & 0x80) != 0)
        {
            if (_position >= _data.Length)
                return false;
            length = ((length & 0x7F) << 8) | _data[_position++];
        }

        // Read value
        if (_position + length > _data.Length)
            return false;

        value = _data.Slice(_position, length);
        _position += length;

        return true;
    }

    /// <summary>
    /// Reads a 32-bit unsigned integer from a tag value
    /// </summary>
    public static uint ReadUInt32(ReadOnlySpan<byte> value)
    {
        if (value.Length != 4)
            throw new ArgumentException("Value must be 4 bytes", nameof(value));
        return BinaryPrimitives.ReadUInt32BigEndian(value);
    }

    /// <summary>
    /// Reads a string from a tag value
    /// </summary>
    public static string ReadString(ReadOnlySpan<byte> value)
    {
        // Remove null terminator if present
        var length = value.Length;
        if (length > 0 && value[length - 1] == 0)
            length--;
        return Encoding.UTF8.GetString(value[..length]);
    }
}
