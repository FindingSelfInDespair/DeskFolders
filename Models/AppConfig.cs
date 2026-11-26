using System.Collections.Generic;

namespace DeskFolders.Models;

public enum TileSizePreset
{
    Compact = 0,
    Standard = 1,
    Large = 2
}

public enum PopupLabelMode
{
    Truncated = 0,
    Full = 1,
    Hidden = 2
}

public enum FolderSortMode
{
    Auto = 0,
    NameAsc = 1,
    DateNewest = 2,
    Custom = 3
}

public enum LaunchpadHotkeyPreset
{
    AltTilde = 0,
    CtrlSpace = 1,
    CtrlShiftSpace = 2,
    AltQ = 3
}

public sealed class AppConfig
{
    public bool TilesVisible { get; set; } = true;
    public TileSizePreset TileSize { get; set; } = TileSizePreset.Standard;
    public int PreviewGridSize { get; set; } = 2;
    public double TileOpacity { get; set; } = 0.82;
    public int TileCornerRadius { get; set; } = 20;
    public int PopupColumns { get; set; } = 4;
    public bool UsePagination { get; set; } = false;
    public bool ShowTileLabels { get; set; } = true;
    public PopupLabelMode PopupLabelMode { get; set; } = PopupLabelMode.Truncated;
    public bool SnapToGrid { get; set; } = true;
    public bool LockPositions { get; set; } = false;
    public bool DeleteToRecycleBin { get; set; } = true;
    public bool EnableSounds { get; set; } = true;
    public bool AutoOrganizeDesktop { get; set; } = false;
    public LaunchpadHotkeyPreset LaunchpadHotkey { get; set; } = LaunchpadHotkeyPreset.AltTilde;
    public List<FolderTileConfig> Tiles { get; set; } = new();
}