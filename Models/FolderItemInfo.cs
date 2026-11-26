using System.Windows.Media.Imaging;

namespace DeskFolders.Models;

public sealed class FolderItemInfo
{
    public string Name { get; init; } = string.Empty;
    public string FullPath { get; init; } = string.Empty;
    public bool IsDirectory { get; init; }
    public BitmapSource? Icon { get; set; }
}