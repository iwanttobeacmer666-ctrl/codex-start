using System.Buffers.Binary;
using System.IO;

namespace CodexStart;

internal static class Mp4
{
    // WPF's reported NaturalDuration can round an MP4 down to the whole second.
    public static double Duration(string path)
    {
        if (!Preferences.IsLocalFile(path) || !string.Equals(Path.GetExtension(path), ".mp4", StringComparison.OrdinalIgnoreCase))
            return 0;
        try
        {
            using var file = File.OpenRead(path);
            while (ReadBox(file, file.Length, out var type, out var end))
            {
                if (type == 0x6d6f6f76) // moov
                {
                    while (ReadBox(file, end, out var child, out var childEnd))
                    {
                        if (child == 0x6d766864) return ReadMovieHeader(file, childEnd); // mvhd
                        file.Position = childEnd;
                    }
                    return 0;
                }
                file.Position = end;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        return 0;
    }

    public static double AspectRatio(string path)
    {
        if (!Preferences.HasVideo(path)) return 0;
        try
        {
            using var file = File.OpenRead(path);
            if (!FindBox(file, file.Length, 0x6d6f6f76, out var movieEnd)) return 0; // moov
            while (FindBox(file, movieEnd, 0x7472616b, out var trackEnd)) // trak
            {
                if (FindBox(file, trackEnd, 0x746b6864, out var headerEnd)) // tkhd
                {
                    var ratio = ReadTrackAspect(file, headerEnd);
                    if (ratio > 0) return ratio;
                }
                file.Position = trackEnd;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        return 0;
    }

    private static bool FindBox(Stream file, long boundary, uint expected, out long end)
    {
        while (ReadBox(file, boundary, out var type, out end))
        {
            if (type == expected) return true;
            file.Position = end;
        }
        return false;
    }

    private static double ReadTrackAspect(Stream file, long end)
    {
        var start = file.Position;
        var matrixOffset = file.ReadByte() switch { 0 => 40, 1 => 52, _ => 0 };
        if (matrixOffset == 0 || end - start < matrixOffset + 44) return 0;
        file.Position = start + matrixOffset;
        Span<byte> layout = stackalloc byte[44];
        file.ReadExactly(layout);
        var width = BinaryPrimitives.ReadUInt32BigEndian(layout[36..]);
        var height = BinaryPrimitives.ReadUInt32BigEndian(layout[40..]);
        if (width == 0 || height == 0) return 0;

        // The track matrix records rotation and scaling in display coordinates.
        var a = (double)BinaryPrimitives.ReadInt32BigEndian(layout);
        var b = (double)BinaryPrimitives.ReadInt32BigEndian(layout[4..]);
        var c = (double)BinaryPrimitives.ReadInt32BigEndian(layout[12..]);
        var d = (double)BinaryPrimitives.ReadInt32BigEndian(layout[16..]);
        var displayWidth = Math.Abs(a) * width + Math.Abs(c) * height;
        var displayHeight = Math.Abs(b) * width + Math.Abs(d) * height;
        return displayWidth > 0 && displayHeight > 0 ? displayWidth / displayHeight : (double)width / height;
    }

    private static bool ReadBox(Stream file, long boundary, out uint type, out long end)
    {
        type = 0;
        end = 0;
        var start = file.Position;
        if (boundary - start < 8) return false;
        Span<byte> header = stackalloc byte[8];
        file.ReadExactly(header);
        type = BinaryPrimitives.ReadUInt32BigEndian(header[4..]);
        ulong size = BinaryPrimitives.ReadUInt32BigEndian(header);
        if (size == 1)
        {
            if (boundary - file.Position < 8) return false;
            file.ReadExactly(header);
            size = BinaryPrimitives.ReadUInt64BigEndian(header);
        }
        if (size == 0) size = (ulong)(boundary - start);
        if (size < (ulong)(file.Position - start) || size > (ulong)(boundary - start)) return false;
        end = start + (long)size;
        return true;
    }

    private static double ReadMovieHeader(Stream file, long end)
    {
        var start = file.Position;
        var version = file.ReadByte();
        var length = version switch { 0 => 20, 1 => 32, _ => 0 };
        if (length == 0 || end - start < length) return 0;
        Span<byte> header = stackalloc byte[32];
        file.Position = start;
        file.ReadExactly(header[..length]);
        var scale = BinaryPrimitives.ReadUInt32BigEndian(header[(version == 0 ? 12 : 20)..]);
        var duration = version == 0 ? BinaryPrimitives.ReadUInt32BigEndian(header[16..])
            : BinaryPrimitives.ReadUInt64BigEndian(header[24..]);
        if (scale == 0 || duration == (version == 0 ? uint.MaxValue : ulong.MaxValue)) return 0;
        return (double)duration / scale;
    }
}
