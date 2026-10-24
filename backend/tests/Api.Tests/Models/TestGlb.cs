using System.Text;
using System.Text.Json.Nodes;

namespace Api.Tests.Models;

/// <summary>A minimal GLB whose only content is one EXT_structural_metadata property table of element ids.</summary>
public static class TestGlb
{
    public static byte[] WithElementIds(string property, params string?[] ids)
    {
        var values = ids.Select(id => Encoding.UTF8.GetBytes(id ?? "")).ToList();
        var offsets = new List<uint> { 0 };
        foreach (var v in values) offsets.Add(offsets[^1] + (uint)v.Length);
        var valueBytes = values.SelectMany(v => v).ToArray();
        var padded = Pad(valueBytes, 0);
        var offsetBytes = offsets.SelectMany(BitConverter.GetBytes).ToArray();
        var bin = padded.Concat(offsetBytes).ToArray();
        var json = new JsonObject
        {
            ["asset"] = new JsonObject { ["version"] = "2.0" },
            ["extensionsUsed"] = new JsonArray("EXT_structural_metadata"),
            ["buffers"] = new JsonArray(new JsonObject { ["byteLength"] = bin.Length }),
            ["bufferViews"] = new JsonArray(
                new JsonObject { ["buffer"] = 0, ["byteOffset"] = 0, ["byteLength"] = valueBytes.Length },
                new JsonObject { ["buffer"] = 0, ["byteOffset"] = padded.Length, ["byteLength"] = offsetBytes.Length }),
            ["extensions"] = new JsonObject
            {
                ["EXT_structural_metadata"] = new JsonObject
                {
                    ["schema"] = new JsonObject { ["classes"] = new JsonObject { ["element"] = new JsonObject { ["properties"] = new JsonObject { [property] = new JsonObject { ["type"] = "STRING" } } } } },
                    ["propertyTables"] = new JsonArray(new JsonObject
                    {
                        ["class"] = "element", ["count"] = ids.Length,
                        ["properties"] = new JsonObject { [property] = new JsonObject { ["values"] = 0, ["stringOffsets"] = 1 } },
                    }),
                },
            },
        };
        var jsonBytes = Pad(Encoding.UTF8.GetBytes(json.ToJsonString()), 0x20);
        var binBytes = Pad(bin, 0);
        using var glb = new MemoryStream();
        using var w = new BinaryWriter(glb);
        w.Write(0x46546C67u);
        w.Write(2u);
        w.Write((uint)(12 + 8 + jsonBytes.Length + 8 + binBytes.Length));
        w.Write((uint)jsonBytes.Length);
        w.Write(0x4E4F534Au);
        w.Write(jsonBytes);
        w.Write((uint)binBytes.Length);
        w.Write(0x004E4942u);
        w.Write(binBytes);
        return glb.ToArray();
    }

    private static byte[] Pad(byte[] bytes, byte fill) =>
        bytes.Concat(Enumerable.Repeat(fill, (4 - bytes.Length % 4) % 4)).ToArray();
}
