using System;
using System.Collections.Generic;

namespace DeskFolders.Models;

public enum FolderCategory
{
    Unassigned = 0,
    None = 1,
    Games = 2,
    Dev = 3,
    Media = 4,
    Software = 5,
    Documents = 6,
    Other = 7,
    Music = 8
}

public sealed class FolderTileConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public string FolderPath { get; set; } = string.Empty;
    public double X { get; set; } = 24;
    public double Y { get; set; } = 24;
    public string TintName { get; set; } = "Dark";
    public string? CustomIconPath { get; set; }
    public FolderCategory Category { get; set; } = FolderCategory.Unassigned;
    public string CustomKeywords { get; set; } = string.Empty;
    public FolderSortMode SortMode { get; set; } = FolderSortMode.Auto;
    public List<string> CustomOrder { get; set; } = new();
    public List<string> HiddenItems { get; set; } = new();
}