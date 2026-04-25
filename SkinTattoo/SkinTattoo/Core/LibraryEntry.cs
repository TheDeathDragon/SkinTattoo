using System;

namespace SkinTattoo.Core;

public sealed class LibraryEntry
{
    public string Hash { get; set; } = "";
    public string FileName { get; set; } = "";
    public string OriginalName { get; set; } = "";
    public string FolderPath { get; set; } = "";
    public bool IsFavorite { get; set; }
    public DateTime AddedAt { get; set; }
    public DateTime LastUsedAt { get; set; }
    public int UseCount { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
}
