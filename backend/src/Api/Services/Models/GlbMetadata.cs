using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace Api.Services.Models;

/// <summary>
/// Reads the string values of one property of the property tables (EXT_structural_metadata) of a GLB – the stable
/// element ids of a segmented model (spec 9.1). Only the JSON chunk and the referenced buffer views are interpreted.
/// </summary>
public static class GlbMetadata
{
    private const uint Magic = 0x46546C67; // glTF
    private const uint JsonChunk = 0x4E4F534A;
    private const uint BinChunk = 0x004E4942;

    /// <summary>Values per property table; null if the GLB has no such property (or no structural metadata).</summary>
    public static List<List<string?>>? ReadProperty(byte[] glb, string property)
    {
        if (glb.Length < 20 || BinaryPrimitives.ReadUInt32LittleEndian(glb) != Magic)
            throw new FormatException("Not a GLB file.");
        ReadOnlyMemory<byte> json = default, bin = default;
        var offset = 12;
        while (offset + 8 <= glb.Length)
        {
            var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(offset));
            var type = BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(offset + 4));
            if (offset + 8 + length > glb.Length) throw new FormatException("Truncated GLB chunk.");
            var data = glb.AsMemory(offset + 8, length);
            if (type == JsonChunk) json = data;
            else if (type == BinChunk) bin = data;
            offset += 8 + length;
        }
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("extensions", out var extensions) || !extensions.TryGetProperty("EXT_structural_metadata", out var metadata)
            || !metadata.TryGetProperty("propertyTables", out var tables))
            return null;

        var views = root.TryGetProperty("bufferViews", out var v) ? v.EnumerateArray().ToList() : [];
        ReadOnlySpan<byte> View(int index)
        {
            var view = views[index];
            if (view.GetProperty("buffer").GetInt32() != 0) throw new FormatException("Only the GLB's own buffer is supported.");
            var start = view.TryGetProperty("byteOffset", out var o) ? o.GetInt32() : 0;
            var length = view.GetProperty("byteLength").GetInt32();
            if (start + length > bin.Length) throw new FormatException("Buffer view outside the binary chunk.");
            return bin.Span.Slice(start, length);
        }

        var result = new List<List<string?>>();
        var found = false;
        foreach (var table in tables.EnumerateArray())
        {
            var count = table.GetProperty("count").GetInt32();
            if (!table.TryGetProperty("properties", out var properties) || !properties.TryGetProperty(property, out var p))
            {
                result.Add(Enumerable.Repeat<string?>(null, count).ToList());
                continue;
            }
            found = true;
            var values = View(p.GetProperty("values").GetInt32());
            if (!p.TryGetProperty("stringOffsets", out var so))
            {
                // Not a string property (e.g. an integer id): not readable as an element id here.
                result.Add(Enumerable.Repeat<string?>(null, count).ToList());
                continue;
            }
            var offsets = View(so.GetInt32());
            var offsetType = p.TryGetProperty("stringOffsetType", out var t) ? t.GetString() : "UINT32";
            var list = new List<string?>(count);
            for (var i = 0; i < count; i++)
            {
                var start = (int)OffsetAt(offsets, offsetType, i);
                var end = (int)OffsetAt(offsets, offsetType, i + 1);
                var text = Encoding.UTF8.GetString(values[start..end]);
                list.Add(string.IsNullOrWhiteSpace(text) ? null : text);
            }
            result.Add(list);
        }
        return found ? result : null;
    }

    private static long OffsetAt(ReadOnlySpan<byte> offsets, string? type, int i) => type switch
    {
        "UINT8" => offsets[i],
        "UINT16" => BinaryPrimitives.ReadUInt16LittleEndian(offsets[(i * 2)..]),
        "UINT64" => (long)BinaryPrimitives.ReadUInt64LittleEndian(offsets[(i * 8)..]),
        _ => BinaryPrimitives.ReadUInt32LittleEndian(offsets[(i * 4)..]),
    };
}
