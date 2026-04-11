using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using SkinTattoo.Core;
using SkinTattoo.Http;

namespace SkinTattoo.Services;

/// <summary>
/// Packs a staging directory + meta into a Penumbra .pmp (zip) file.
/// .pmp layout: meta.json + default_mod.json at root, plus all staging files
/// at their game-path mirrored locations.
/// </summary>
public static class PmpPackageWriter
{
    /// <summary>
    /// Build a .pmp zip at outputPmpPath. Overwrites if exists.
    /// </summary>
    /// <param name="stagingDir">Directory containing files at game-path mirrored layout.</param>
    /// <param name="options">Mod metadata (name, author, etc.).</param>
    /// <param name="redirects">gamePath -> relative disk path (forward slashes) inside the mod.</param>
    /// <param name="outputPmpPath">Destination .pmp file path.</param>
    public static void Pack(string stagingDir, ModExportOptions options,
        Dictionary<string, string> redirects, string outputPmpPath)
    {
        var parent = Path.GetDirectoryName(outputPmpPath);
        if (!string.IsNullOrEmpty(parent))
            Directory.CreateDirectory(parent);

        // FileMode.Create overwrites existing files atomically
        using var fs = new FileStream(outputPmpPath, FileMode.Create);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);

        WriteJsonEntry(zip, "meta.json", w => WriteMeta(w, options));
        WriteJsonEntry(zip, "default_mod.json", w => WriteDefaultMod(w, redirects));

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

    /// <summary>meta.json (Penumbra FileVersion 3  -- matches ModMeta.cs).</summary>
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

    /// <summary>
    /// default_mod.json (Penumbra DefaultSubMod.WriteModContainer schema).
    /// Both keys (game paths) and values (relative paths) use forward slashes.
    /// </summary>
    private static void WriteDefaultMod(Utf8JsonWriter w, Dictionary<string, string> redirects)
    {
        w.WriteStartObject();
        w.WriteNumber("Version", 0);
        w.WriteString("Name", "Default");
        w.WriteString("Description", "");
        w.WriteNumber("Priority", 0);

        w.WritePropertyName("Files");
        w.WriteStartObject();
        foreach (var (gamePath, relPath) in redirects)
            w.WriteString(gamePath.Replace('\\', '/'), relPath.Replace('\\', '/'));
        w.WriteEndObject();

        w.WritePropertyName("FileSwaps");
        w.WriteStartObject();
        w.WriteEndObject();

        w.WritePropertyName("Manipulations");
        w.WriteStartArray();
        w.WriteEndArray();

        w.WriteEndObject();
    }
}
