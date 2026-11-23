using System.Text.RegularExpressions;
using YamlDotNet.RepresentationModel;

namespace AssetManager.Processing.Handlers;

/// <summary>Texture references of a Unity <c>.mat</c> file (YAML with <c>!u!</c> tags).</summary>
public sealed record UnityMaterialInfo(string Name, IReadOnlyList<(string Property, string Guid, string? Slot)> Textures);

public static partial class UnityMaterial
{
    /// <summary>Maps shader texture properties to renderer slots; other properties are companions without a slot.</summary>
    private static readonly Dictionary<string, string> SlotByProperty = new(StringComparer.Ordinal)
    {
        ["_MainTex"] = "map", ["_BaseMap"] = "map", ["_BaseColorMap"] = "map", ["_AlbedoMap"] = "map", ["_Albedo"] = "map", ["_MainTexture"] = "map",
        ["_BumpMap"] = "normalMap", ["_NormalMap"] = "normalMap", ["_DetailNormalMap"] = "normalMap",
        ["_EmissionMap"] = "emissiveMap", ["_EmissiveColorMap"] = "emissiveMap",
    };

    /// <summary>Parses a .mat file; returns null when it is not a readable text material.</summary>
    public static UnityMaterialInfo? Read(string path)
    {
        string text;
        try { text = File.ReadAllText(path); }
        catch (IOException) { return null; }
        return Parse(text, Path.GetFileNameWithoutExtension(path));
    }

    public static UnityMaterialInfo? Parse(string text, string fallbackName)
    {
        if (!text.StartsWith("%YAML", StringComparison.Ordinal)) return null; // binary-serialized material
        // Unity's "--- !u!21 &2100000" document headers are not standard YAML tags.
        var cleaned = UnityTag().Replace(text, "---");
        cleaned = string.Join('\n', cleaned.Split('\n').Where(l => !l.StartsWith("%TAG", StringComparison.Ordinal)));
        var stream = new YamlStream();
        try
        {
            stream.Load(new StringReader(cleaned));
        }
        catch (YamlDotNet.Core.YamlException)
        {
            return null;
        }

        foreach (var doc in stream.Documents)
        {
            if (doc.RootNode is not YamlMappingNode root || !root.Children.TryGetValue(new YamlScalarNode("Material"), out var matNode) ||
                matNode is not YamlMappingNode mat)
                continue;
            var name = Scalar(mat, "m_Name") is { Length: > 0 } n ? n : fallbackName;
            var textures = new List<(string, string, string?)>();
            if (Child(mat, "m_SavedProperties") is YamlMappingNode props && Child(props, "m_TexEnvs") is { } envs)
            {
                // Newer Unity: a sequence of single-key maps; older: a mapping of property → data.
                IEnumerable<KeyValuePair<YamlNode, YamlNode>> entries = envs switch
                {
                    YamlSequenceNode seq => seq.Children.OfType<YamlMappingNode>().SelectMany(m => m.Children),
                    YamlMappingNode map => map.Children,
                    _ => [],
                };
                foreach (var (key, value) in entries)
                {
                    if (key is not YamlScalarNode { Value: { } prop } || value is not YamlMappingNode data) continue;
                    if (Child(data, "m_Texture") is not YamlMappingNode tex || Scalar(tex, "guid") is not { Length: 32 } guid) continue;
                    if (guid.All(c => c == '0')) continue;
                    textures.Add((prop, guid, SlotByProperty.GetValueOrDefault(prop)));
                }
            }
            return new UnityMaterialInfo(name, textures);
        }
        return null;
    }

    /// <summary>Unity GUID from a <c>.meta</c> file.</summary>
    public static string? GuidFromMeta(string metaPath)
    {
        try
        {
            foreach (var line in File.ReadLines(metaPath).Take(10))
                if (line.StartsWith("guid:", StringComparison.Ordinal)) return line[5..].Trim();
        }
        catch (IOException) { }
        return null;
    }

    private static YamlNode? Child(YamlMappingNode node, string key) =>
        node.Children.TryGetValue(new YamlScalarNode(key), out var v) ? v : null;

    private static string? Scalar(YamlMappingNode node, string key) => (Child(node, key) as YamlScalarNode)?.Value;

    [GeneratedRegex(@"^--- !u!\d+ &-?\d+( stripped)?", RegexOptions.Multiline)]
    private static partial Regex UnityTag();
}
