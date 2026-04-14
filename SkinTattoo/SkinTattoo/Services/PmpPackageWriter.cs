using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using SkinTattoo.Core;

namespace SkinTattoo.Services;

/// <summary>
/// Packs a staging directory + meta into a Penumbra .pmp (zip) file.
///
/// Layout:
///   meta.json                   mod metadata
///   default_mod.json            always-on files (shared shaders)
///   group_001_<ModName>.json    Type=Multi, one option per selected TargetGroup
///   &lt;game-path mirrored files&gt;
/// </summary>
public static class PmpPackageWriter
{
    internal static void Pack(string stagingDir, ModExportOptions options,
        Dictionary<string, string> sharedRedirects,
        List<GroupExport> groups,
        string outputPmpPath)
    {
        var parent = Path.GetDirectoryName(outputPmpPath);
        if (!string.IsNullOrEmpty(parent))
            Directory.CreateDirectory(parent);

        using var fs = new FileStream(outputPmpPath, FileMode.Create);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);

        WriteJsonEntry(zip, "meta.json", w => WriteMeta(w, options));
        WriteJsonEntry(zip, "default_mod.json", w => WriteDefaultMod(w, sharedRedirects));

        if (groups.Count > 0)
        {
            var groupFileName = $"group_001_{SanitizeFileName(options.ModName)}.json";
            WriteJsonEntry(zip, groupFileName, w => WriteDecalsGroup(w, options, groups));
        }

        if (Directory.Exists(stagingDir))
        {
            var rootLen = stagingDir.Length + 1;
            foreach (var file in Directory.EnumerateFiles(stagingDir, "*", SearchOption.AllDirectories))
            {
                var rel = file.Substring(rootLen).Replace('\\', '/');
                var entry = zip.CreateEntry(rel, CompressionLevel.Fastest);
                using var es = entry.Open();
                using var rs = File.OpenRead(file);
                rs.CopyTo(es);
            }
        }
    }

    private static void WriteJsonEntry(ZipArchive zip, string name, System.Action<Utf8JsonWriter> body)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Fastest);
        using var s = entry.Open();
        using var w = new Utf8JsonWriter(s, new JsonWriterOptions { Indented = true });
        body(w);
    }

    private static void WriteMeta(Utf8JsonWriter w, ModExportOptions options)
    {
        w.WriteStartObject();
        w.WriteNumber("FileVersion", 3);
        w.WriteString("Name", options.ModName);
        w.WriteString("Author", options.Author);
        w.WriteString("Description", options.Description);
        w.WriteString("Image", "");
        w.WriteString("Version", options.Version);
        w.WriteString("Website", "");
        w.WriteStartArray("ModTags");
        w.WriteEndArray();
        w.WriteEndObject();
    }

    private static void WriteDefaultMod(Utf8JsonWriter w, Dictionary<string, string> sharedRedirects)
    {
        w.WriteStartObject();
        w.WriteString("Name", "");
        w.WriteString("Description", "");

        w.WritePropertyName("Files");
        w.WriteStartObject();
        foreach (var (gamePath, relPath) in sharedRedirects)
            w.WriteString(ToForward(gamePath), ToForward(relPath));
        w.WriteEndObject();

        w.WritePropertyName("FileSwaps");
        w.WriteStartObject();
        w.WriteEndObject();

        w.WritePropertyName("Manipulations");
        w.WriteStartArray();
        w.WriteEndArray();

        w.WriteEndObject();
    }

    // Type=Multi so each decal group can be independently toggled in Penumbra's UI.
    // Priority 0 on all options — they target non-overlapping materials in practice,
    // and letting users reorder in Penumbra covers any conflict.
    private static void WriteDecalsGroup(Utf8JsonWriter w, ModExportOptions options, List<GroupExport> groups)
    {
        w.WriteStartObject();
        w.WriteNumber("Version", 0);
        w.WriteString("Name", options.ModName);
        w.WriteString("Description", "");
        w.WriteString("Image", "");
        w.WriteNumber("Priority", 0);
        w.WriteString("Type", "Multi");
        w.WriteNumber("DefaultSettings", (1L << groups.Count) - 1);

        w.WritePropertyName("Options");
        w.WriteStartArray();
        for (int i = 0; i < groups.Count; i++)
        {
            var g = groups[i];
            w.WriteStartObject();
            w.WriteString("Name", string.IsNullOrWhiteSpace(g.Name) ? $"图层组 {i + 1}" : g.Name);
            w.WriteString("Description", "");
            w.WriteNumber("Priority", 0);

            w.WritePropertyName("Files");
            w.WriteStartObject();
            foreach (var (gamePath, relPath) in g.Files)
                w.WriteString(ToForward(gamePath), ToForward(relPath));
            w.WriteEndObject();

            w.WritePropertyName("FileSwaps");
            w.WriteStartObject();
            w.WriteEndObject();

            w.WritePropertyName("Manipulations");
            w.WriteStartArray();
            w.WriteEndArray();

            w.WriteEndObject();
        }
        w.WriteEndArray();

        w.WriteEndObject();
    }

    private static string ToForward(string p) => p.Replace('\\', '/');

    private static string SanitizeFileName(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "decals";
        var invalid = Path.GetInvalidFileNameChars();
        var buf = new System.Text.StringBuilder(s.Length);
        foreach (var ch in s)
            buf.Append(System.Array.IndexOf(invalid, ch) >= 0 ? '_' : ch);
        var result = buf.ToString().Trim();
        return string.IsNullOrEmpty(result) ? "decals" : result;
    }
}
