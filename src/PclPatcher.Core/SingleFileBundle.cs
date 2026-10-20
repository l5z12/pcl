using System.Buffers.Binary;
using System.Text;

namespace PclPatcher.Core;

// A .NET single-file bundle (manifest v6, as written by Microsoft.NET.HostModel): the apphost, the bundled
// files, then a manifest. The apphost stores the manifest offset in the 8 bytes before the bundle signature.
internal sealed class SingleFileBundle
{
    public const byte AssemblyType = 1, DepsJsonType = 3, RuntimeConfigType = 4;
    private const int Alignment = 4096;
    // SHA-256 of ".net core bundle".
    private static readonly byte[] Signature =
    [
        0x8b, 0x12, 0x02, 0xb9, 0x6a, 0x61, 0x20, 0x38, 0x72, 0x7b, 0x93, 0x02, 0x14, 0xd7, 0xa0, 0x32,
        0x13, 0xf5, 0xb9, 0xe6, 0xef, 0xae, 0x33, 0x18, 0xee, 0x3b, 0x2d, 0xce, 0x24, 0xb3, 0x6a, 0xae
    ];

    // Field is the manifest position of the entry's offset, followed by its size and compressed size.
    public sealed record Entry(string Path, byte Type, long Offset, long Size, long CompressedSize, int Field)
    {
        public long StoredSize => CompressedSize != 0 ? CompressedSize : Size;
    }

    private readonly byte[] bytes;
    private readonly int markerAt, jsonFields;
    private readonly long manifestAt, depsOffset, configOffset;
    public IReadOnlyList<Entry> Entries { get; }

    private SingleFileBundle(byte[] bytes, int markerAt)
    {
        this.bytes = bytes;
        this.markerAt = markerAt;
        manifestAt = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(markerAt));
        if (manifestAt <= markerAt || manifestAt >= bytes.Length)
            throw new InvalidDataException("the manifest offset is out of range");
        using var reader = new BinaryReader(new MemoryStream(bytes, false), Encoding.UTF8);
        reader.BaseStream.Position = manifestAt;
        var major = reader.ReadUInt32();
        reader.ReadUInt32();
        if (major != 6) throw new InvalidDataException($"bundle version {major} is unsupported");
        var count = reader.ReadInt32();
        if (count is < 1 or > 4096) throw new InvalidDataException("the file count is out of range");
        reader.ReadString(); // bundle ID
        jsonFields = (int)reader.BaseStream.Position;
        depsOffset = reader.ReadInt64();
        var depsSize = reader.ReadInt64();
        configOffset = reader.ReadInt64();
        var configSize = reader.ReadInt64();
        reader.ReadUInt64(); // flags
        var entries = new List<Entry>(count);
        for (var i = 0; i < count; i++)
        {
            var field = (int)reader.BaseStream.Position;
            long offset = reader.ReadInt64(), size = reader.ReadInt64(), compressed = reader.ReadInt64();
            var type = reader.ReadByte();
            entries.Add(new(reader.ReadString(), type, offset, size, compressed, field));
        }
        var previousEnd = (long)markerAt + 8 + Signature.Length;
        foreach (var entry in entries.OrderBy(e => e.Offset))
        {
            if (entry.Size < 0 || entry.CompressedSize < 0 || entry.Offset < previousEnd || entry.Offset + entry.StoredSize > manifestAt)
                throw new InvalidDataException($"{entry.Path} lies outside the bundle or overlaps another file");
            previousEnd = entry.Offset + entry.StoredSize;
        }
        if (depsOffset != 0 && !entries.Any(e => e.Type == DepsJsonType && e.Offset == depsOffset && e.Size == depsSize) ||
            configOffset != 0 && !entries.Any(e => e.Type == RuntimeConfigType && e.Offset == configOffset && e.Size == configSize))
            throw new InvalidDataException("the manifest's JSON locations don't match its file list");
        Entries = entries;
    }

    // Null when the file isn't a bundle at all; InvalidDataException when it is one but can't be read.
    public static SingleFileBundle? TryOpen(byte[] bytes)
    {
        // The signature also turns up as plain data (this assembly has a copy), so only count an occurrence
        // whose stored offset points past it into the file.
        for (var at = bytes.AsSpan().IndexOf(Signature); at >= 0; at = NextSignature(bytes, at))
        {
            if (at < 8) continue;
            var manifest = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(at - 8));
            if (manifest <= at || manifest >= bytes.Length) continue;
            try { return new SingleFileBundle(bytes, at - 8); }
            catch (Exception e) when (e is IOException or FormatException or ArgumentException)
            {
                throw new InvalidDataException("the manifest is truncated or malformed", e);
            }
        }
        return null;
    }

    private static int NextSignature(byte[] bytes, int previous)
    {
        var next = bytes.AsSpan(previous + 1).IndexOf(Signature);
        return next < 0 ? -1 : previous + 1 + next;
    }

    public ReadOnlySpan<byte> Read(Entry entry) => bytes.AsSpan((int)entry.Offset, (int)entry.StoredSize);

    // Swaps one uncompressed file. Everything after it moves by whole 4 KB pages, so later files keep the
    // alignment the bundler gave them and only the manifest's fixed-width offsets change.
    public byte[] Replace(Entry target, byte[] content)
    {
        if (target.CompressedSize != 0) throw new InvalidDataException($"{target.Path} is compressed");
        var end = target.Offset + target.Size;
        var growth = content.Length - target.Size;
        var shift = growth > 0 ? (growth + Alignment - 1) / Alignment * Alignment : -(-growth / Alignment * Alignment);
        var output = new byte[bytes.Length + shift];
        bytes.AsSpan(0, (int)target.Offset).CopyTo(output);
        content.CopyTo(output, target.Offset);
        bytes.AsSpan((int)end).CopyTo(output.AsSpan((int)(end + shift)));

        long Move(long offset) => offset >= end ? offset + shift : offset;
        void Write(long at, long value) => BinaryPrimitives.WriteInt64LittleEndian(output.AsSpan((int)Move(at)), value);
        Write(markerAt, Move(manifestAt));
        Write(jsonFields, Move(depsOffset));
        Write(jsonFields + 16, Move(configOffset));
        foreach (var entry in Entries) Write(entry.Field, Move(entry.Offset));
        Write(target.Field + 8, content.Length);
        return output;
    }
}
