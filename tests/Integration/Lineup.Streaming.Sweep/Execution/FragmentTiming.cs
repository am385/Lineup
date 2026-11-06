using System.Buffers.Binary;

namespace Lineup.Streaming.Sweep;

/// <summary>Timing defaults and edit-list normalization for one independent MP4 track.</summary>
internal sealed record FragmentInitialization(uint TrackId, uint Timescale, uint DefaultDuration, double EditOffsetSeconds)
{
    /// <summary>Gets the precision of a movie clock contributing an empty edit to the presentation offset; zero when unused.</summary>
    public double EditPrecisionSeconds { get; init; }
}

/// <summary>Decoded sample timing and composition-aware presentation coverage in one fragment.</summary>
internal sealed record FragmentTimingSample(long DecodeStart, long DecodeEnd, double PresentationStart, double PresentationEnd, long MaximumDecodeGap);

/// <summary>Reads ISO BMFF timing boxes without decoding media or treating arrival time as presentation time.</summary>
internal static class FragmentTiming
{
    /// <summary>Reads the track clock, defaults and a single media edit optionally preceded by an empty edit.</summary>
    public static FragmentInitialization ReadInitialization(byte[] data)
    {
        var moov = Required(data, 0, data.Length, "moov");
        var tracks = Boxes(data, moov.Body, moov.End).Where(box => box.Type == "trak").ToArray();
        if (tracks.Length != 1)
        {
            throw new InvalidDataException("Independent initialization must contain exactly one track.");
        }
        var trak = tracks[0];
        var tkhd = Required(data, trak.Body, trak.End, "tkhd");
        var mdia = Required(data, trak.Body, trak.End, "mdia");
        var mdhd = Required(data, mdia.Body, mdia.End, "mdhd");
        var trackId = U32(data, tkhd, Version(data, tkhd) == 1 ? 20 : 12);
        var timescale = U32(data, mdhd, Version(data, mdhd) == 1 ? 20 : 12);
        if (timescale == 0)
        {
            throw new InvalidDataException("MP4 track timescale must be positive.");
        }
        uint defaultDuration = 0;
        var mvex = Optional(data, moov.Body, moov.End, "mvex");
        if (mvex is not null)
        {
            var trex = Required(data, mvex.Body, mvex.End, "trex");
            if (U32(data, trex, 4) != trackId)
            {
                throw new InvalidDataException("Initialization defaults refer to another track.");
            }
            defaultDuration = U32(data, trex, 12);
        }
        double editOffset = 0;
        double editPrecision = 0;
        var edts = Optional(data, trak.Body, trak.End, "edts");
        if (edts is not null)
        {
            var elst = Required(data, edts.Body, edts.End, "elst");
            var movie = Required(data, moov.Body, moov.End, "mvhd");
            var movieScale = U32(data, movie, Version(data, movie) == 1 ? 20 : 12);
            var count = U32(data, elst, 4);
            if (movieScale == 0 || count is < 1 or > 2)
            {
                throw new InvalidDataException("Unsupported movie clock or non-linear edit list.");
            }
            var wide = Version(data, elst) == 1;
            var offset = 8;
            var mediaEdits = 0;
            for (var index = 0; index < count; index++)
            {
                var duration = wide ? U64(data, elst, offset) : U32(data, elst, offset);
                var mediaTime = wide ? unchecked((long)U64(data, elst, offset + 8)) : unchecked((int)U32(data, elst, offset + 4));
                var rate = U32(data, elst, offset + (wide ? 16 : 8));
                if (rate != 0x10000 || mediaTime < -1 || (mediaTime == -1 && index != 0) || (mediaTime >= 0 && ++mediaEdits != 1))
                {
                    throw new InvalidDataException("Unsupported edit rate or non-linear edit list.");
                }
                editOffset += mediaTime == -1 ? duration / (double)movieScale : -mediaTime / (double)timescale;
                if (mediaTime == -1 && duration > 0)
                {
                    editPrecision = 1.0 / movieScale;
                }
                offset += wide ? 20 : 12;
            }
            if (mediaEdits != 1)
            {
                throw new InvalidDataException("Edit list has no playable media interval.");
            }
        }
        return new(trackId, timescale, defaultDuration, editOffset) { EditPrecisionSeconds = editPrecision };
    }

    /// <summary>Reads decode times, sample durations and signed or unsigned composition offsets with bounds checks.</summary>
    public static FragmentTimingSample ReadFragment(byte[] data, FragmentInitialization initialization)
    {
        long? firstDecode = null;
        long lastDecode = 0;
        long maximumGap = 0;
        var firstPresentation = double.PositiveInfinity;
        var lastPresentation = double.NegativeInfinity;
        var samples = 0;
        foreach (var moof in Boxes(data, 0, data.Length).Where(box => box.Type == "moof"))
        {
            foreach (var traf in Boxes(data, moof.Body, moof.End).Where(box => box.Type == "traf"))
            {
                var tfhd = Required(data, traf.Body, traf.End, "tfhd");
                if (U32(data, tfhd, 4) != initialization.TrackId)
                {
                    throw new InvalidDataException("Fragment belongs to another initialized track.");
                }
                var flags = U32(data, tfhd, 0) & 0xFFFFFF;
                var offset = 8 + ((flags & 1) != 0 ? 8 : 0) + ((flags & 2) != 0 ? 4 : 0);
                var defaultDuration = (flags & 8) != 0 ? U32(data, tfhd, offset) : initialization.DefaultDuration;
                var tfdt = Required(data, traf.Body, traf.End, "tfdt");
                var rawDecode = Version(data, tfdt) == 1 ? U64(data, tfdt, 4) : U32(data, tfdt, 4);
                if (rawDecode > long.MaxValue)
                {
                    throw new InvalidDataException("Decode time exceeds the supported clock range.");
                }
                var decode = (long)rawDecode;
                if (firstDecode is not null)
                {
                    maximumGap = Math.Max(maximumGap, Math.Abs(decode - lastDecode));
                }
                firstDecode ??= decode;
                foreach (var trun in Boxes(data, traf.Body, traf.End).Where(box => box.Type == "trun"))
                {
                    var runFlags = U32(data, trun, 0) & 0xFFFFFF;
                    var count = U32(data, trun, 4);
                    if (count > 1_000_000)
                    {
                        throw new InvalidDataException("Fragment sample count exceeds the inspection limit.");
                    }
                    offset = 8 + ((runFlags & 1) != 0 ? 4 : 0) + ((runFlags & 4) != 0 ? 4 : 0);
                    for (var sample = 0; sample < count; sample++)
                    {
                        var duration = defaultDuration;
                        if ((runFlags & 0x100) != 0)
                        {
                            duration = U32(data, trun, offset);
                            offset += 4;
                        }
                        offset += ((runFlags & 0x200) != 0 ? 4 : 0) + ((runFlags & 0x400) != 0 ? 4 : 0);
                        long composition = 0;
                        if ((runFlags & 0x800) != 0)
                        {
                            var value = U32(data, trun, offset);
                            composition = Version(data, trun) == 1 ? unchecked((int)value) : value;
                            offset += 4;
                        }
                        if (offset > trun.End - trun.Body || duration == 0 || decode > long.MaxValue - duration)
                        {
                            throw new InvalidDataException("Missing, truncated or overflowing sample timing.");
                        }
                        var presentation = decode / (double)initialization.Timescale + composition / (double)initialization.Timescale + initialization.EditOffsetSeconds;
                        firstPresentation = Math.Min(firstPresentation, presentation);
                        lastPresentation = Math.Max(lastPresentation, presentation + duration / (double)initialization.Timescale);
                        decode += duration;
                        samples++;
                    }
                }
                lastDecode = decode;
            }
        }
        return samples > 0
            ? new(firstDecode!.Value, lastDecode, firstPresentation, lastPresentation, maximumGap)
            : throw new InvalidDataException("Fragment omitted required sample timing.");
    }

    private static byte Version(byte[] data, Box box)
    {
        var version = (byte)(U32(data, box, 0) >> 24);
        return version <= 1 ? version : throw new InvalidDataException("Unsupported timing box version.");
    }

    private static uint U32(byte[] data, Box box, int offset)
    {
        if (offset < 0 || offset > box.End - box.Body - 4)
        {
            throw new InvalidDataException($"Truncated {box.Type} timing field.");
        }
        return BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(box.Body + offset, 4));
    }

    private static ulong U64(byte[] data, Box box, int offset) => ((ulong)U32(data, box, offset) << 32) | U32(data, box, offset + 4);

    private static Box Required(byte[] data, int start, int end, string type) =>
        Optional(data, start, end, type) ?? throw new InvalidDataException($"Missing MP4 {type} timing box.");

    private static Box? Optional(byte[] data, int start, int end, string type) => Boxes(data, start, end).FirstOrDefault(box => box.Type == type);

    private static IEnumerable<Box> Boxes(byte[] data, int start, int end)
    {
        for (var offset = start; offset < end;)
        {
            if (end - offset < 8)
            {
                throw new InvalidDataException("Truncated MP4 box header.");
            }
            ulong size = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, 4));
            var header = 8;
            if (size == 1)
            {
                if (end - offset < 16)
                {
                    throw new InvalidDataException("Truncated extended MP4 box header.");
                }
                size = BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(offset + 8, 8));
                header = 16;
            }
            if (size == 0)
            {
                size = (ulong)(end - offset);
            }
            if (size < (ulong)header || size > (ulong)(end - offset))
            {
                throw new InvalidDataException("Invalid MP4 box extent.");
            }
            yield return new(Encoding.ASCII.GetString(data, offset + 4, 4), offset + header, offset + (int)size);
            offset += (int)size;
        }
    }

    private sealed record Box(string Type, int Body, int End);
}
