using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Windows.Media;

namespace DeskFolders.Services;

public sealed record TileThemePalette(
    string Key,
    string DisplayName,
    Brush TileBackground,
    Brush TileHoverBackground,
    Brush PopupBackground,
    Color PrimaryText,
    Color SecondaryText,
    Brush BorderBrush);

public static class TileThemeService
{
    private static readonly ConcurrentDictionary<string, TileThemePalette> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static readonly (string Key, string Name)[] PresetInfos =
    {
        ("Dark", "Тёмное стекло"),
        ("Light", "Светлое стекло"),
        ("Blue", "Сапфировое стекло"),
        ("Purple", "Фиолетовое стекло"),
        ("Emerald", "Изумрудное стекло"),
        ("Amber", "Янтарное стекло"),
        ("Rose", "Рубиновое стекло")
    };

    public static IReadOnlyList<TileThemePalette> Palettes { get; } = BuildBasePalettes();

    private static List<TileThemePalette> BuildBasePalettes()
    {
        var list = new List<TileThemePalette>();
        foreach (var (k, _) in PresetInfos)
            list.Add(Get(k, 0.82));
        return list;
    }

    public static TileThemePalette Get(string? key, double opacity = 0.82)
    {
        string normKey = string.IsNullOrWhiteSpace(key) ? "Dark" : key;
        int opPct = Math.Clamp((int)Math.Round(opacity * 100), 25, 100);
        string cacheKey = $"{normKey}|{opPct}";

        return Cache.GetOrAdd(cacheKey, _ =>
        {
            double factor = opPct / 100.0;
            if (normKey.StartsWith('#') && TryParseHex(normKey, out var customColor))
                return CreateFromColor(customColor, normKey, factor);

            return CreatePreset(normKey, factor);
        });
    }

    private static TileThemePalette CreatePreset(string key, double op) => key switch
    {
        "Light" => Create("Light", "Светлое стекло",
            WithAlpha(0xF6, 0xF6, 0xFA, 0xE8, op), WithAlpha(0xE2, 0xE4, 0xEC, 0xD2, op),
            WithAlpha(0xFF, 0xFF, 0xFF, 0xF5, op), WithAlpha(0xEC, 0xEE, 0xF4, 0xE6, op),
            Color.FromArgb(ScaleAlpha(0xF4, Math.Max(0.82, op)), 0xF2, 0xF3, 0xF7),
            Color.FromRgb(0x1C, 0x1C, 0x1E), Color.FromRgb(0x55, 0x55, 0x5C),
            Color.FromArgb(0x88, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x30, 0x00, 0x00, 0x00)),

        "Blue" => Create("Blue", "Сапфировое стекло",
            WithAlpha(0x19, 0x30, 0x52, 0xEE, op), WithAlpha(0x10, 0x1E, 0x36, 0xDC, op),
            WithAlpha(0x22, 0x3E, 0x68, 0xF5, op), WithAlpha(0x16, 0x28, 0x44, 0xE8, op),
            Color.FromArgb(ScaleAlpha(0xF6, Math.Max(0.84, op)), 0x13, 0x21, 0x38),
            Color.FromRgb(0xF5, 0xF8, 0xFF), Color.FromRgb(0x9E, 0xB8, 0xD9),
            Color.FromArgb(0x58, 0x82, 0xB8, 0xFF), Color.FromArgb(0x1E, 0x82, 0xB8, 0xFF)),

        "Purple" => Create("Purple", "Фиолетовое стекло",
            WithAlpha(0x34, 0x1F, 0x4E, 0xEE, op), WithAlpha(0x21, 0x13, 0x34, 0xDC, op),
            WithAlpha(0x44, 0x28, 0x64, 0xF5, op), WithAlpha(0x2B, 0x18, 0x42, 0xE8, op),
            Color.FromArgb(ScaleAlpha(0xF6, Math.Max(0.84, op)), 0x22, 0x15, 0x36),
            Color.FromRgb(0xF8, 0xF5, 0xFF), Color.FromRgb(0xBA, 0xA2, 0xD8),
            Color.FromArgb(0x58, 0xBD, 0x88, 0xFF), Color.FromArgb(0x1E, 0xBD, 0x88, 0xFF)),

        "Emerald" => Create("Emerald", "Изумрудное стекло",
            WithAlpha(0x15, 0x3A, 0x30, 0xEE, op), WithAlpha(0x0E, 0x26, 0x20, 0xDC, op),
            WithAlpha(0x1C, 0x4B, 0x3E, 0xF5, op), WithAlpha(0x12, 0x32, 0x29, 0xE8, op),
            Color.FromArgb(ScaleAlpha(0xF6, Math.Max(0.84, op)), 0x11, 0x28, 0x22),
            Color.FromRgb(0xF2, 0xFF, 0xFA), Color.FromRgb(0x93, 0xC8, 0xB6),
            Color.FromArgb(0x58, 0x6E, 0xE7, 0xB7), Color.FromArgb(0x1E, 0x6E, 0xE7, 0xB7)),

        "Amber" => Create("Amber", "Янтарное стекло",
            WithAlpha(0x46, 0x2B, 0x14, 0xEE, op), WithAlpha(0x2D, 0x1A, 0x0C, 0xDC, op),
            WithAlpha(0x58, 0x36, 0x19, 0xF5, op), WithAlpha(0x38, 0x22, 0x10, 0xE8, op),
            Color.FromArgb(ScaleAlpha(0xF6, Math.Max(0.84, op)), 0x2B, 0x1B, 0x0E),
            Color.FromRgb(0xFF, 0xF9, 0xF2), Color.FromRgb(0xD9, 0xB4, 0x8F),
            Color.FromArgb(0x58, 0xFF, 0xB8, 0x6C), Color.FromArgb(0x1E, 0xFF, 0xB8, 0x6C)),

        "Rose" => Create("Rose", "Рубиновое стекло",
            WithAlpha(0x4B, 0x1B, 0x2B, 0xEE, op), WithAlpha(0x31, 0x11, 0x1C, 0xDC, op),
            WithAlpha(0x5E, 0x22, 0x36, 0xF5, op), WithAlpha(0x3D, 0x16, 0x23, 0xE8, op),
            Color.FromArgb(ScaleAlpha(0xF6, Math.Max(0.84, op)), 0x2E, 0x12, 0x1B),
            Color.FromRgb(0xFF, 0xF4, 0xF7), Color.FromRgb(0xD8, 0x9C, 0xAE),
            Color.FromArgb(0x58, 0xFF, 0x80, 0xA8), Color.FromArgb(0x1E, 0xFF, 0x80, 0xA8)),

        _ => Create("Dark", "Тёмное стекло",
            WithAlpha(0x22, 0x22, 0x27, 0xF0, op), WithAlpha(0x16, 0x16, 0x1A, 0xDA, op),
            WithAlpha(0x2D, 0x2D, 0x33, 0xF8, op), WithAlpha(0x1F, 0x1F, 0x24, 0xE8, op),
            Color.FromArgb(ScaleAlpha(0xF6, Math.Max(0.84, op)), 0x1B, 0x1B, 0x20),
            Color.FromRgb(0xF5, 0xF5, 0xF7), Color.FromRgb(0x98, 0x98, 0x9F),
            Color.FromArgb(0x48, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x16, 0xFF, 0xFF, 0xFF))
    };

    public static bool TryParseHex(string? hex, out Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(hex))
            return false;

        try
        {
            if (ColorConverter.ConvertFromString(hex.Trim()) is Color parsed)
            {
                color = Color.FromRgb(parsed.R, parsed.G, parsed.B);
                return true;
            }
        }
        catch
        {
        }

        return false;
    }

    public static TileThemePalette CreateFromColor(Color baseColor, string? keyOverride = null, double opacity = 0.82)
    {
        string key = keyOverride ?? $"#{baseColor.R:X2}{baseColor.G:X2}{baseColor.B:X2}";

        Color bgTop = ScaleDark(baseColor, 0.44, ScaleAlpha(0xEE, opacity));
        Color bgBottom = ScaleDark(baseColor, 0.27, ScaleAlpha(0xDC, opacity));
        Color hoverTop = ScaleDark(baseColor, 0.56, ScaleAlpha(0xF6, opacity));
        Color hoverBottom = ScaleDark(baseColor, 0.36, ScaleAlpha(0xE8, opacity));
        Color popupBg = ScaleDark(baseColor, 0.24, ScaleAlpha(0xF6, Math.Max(0.84, opacity)));

        Color borderTop = Color.FromArgb(
            0x62,
            (byte)Math.Min(255, baseColor.R / 2 + 128),
            (byte)Math.Min(255, baseColor.G / 2 + 128),
            (byte)Math.Min(255, baseColor.B / 2 + 128));

        Color borderBottom = Color.FromArgb(0x22, baseColor.R, baseColor.G, baseColor.B);

        Color secondary = Color.FromRgb(
            (byte)Math.Min(255, baseColor.R / 3 + 145),
            (byte)Math.Min(255, baseColor.G / 3 + 145),
            (byte)Math.Min(255, baseColor.B / 3 + 145));

        return Create(
            key,
            $"Свой ({key})",
            bgTop, bgBottom,
            hoverTop, hoverBottom,
            popupBg,
            Color.FromRgb(0xF5, 0xF5, 0xF7),
            secondary,
            borderTop, borderBottom);
    }

    private static Color WithAlpha(byte r, byte g, byte b, byte baseAlpha, double factor) =>
        Color.FromArgb(ScaleAlpha(baseAlpha, factor), r, g, b);

    private static byte ScaleAlpha(byte baseAlpha, double factor) =>
        (byte)Math.Clamp((int)Math.Round(baseAlpha * factor), 40, 255);

    private static Color ScaleDark(Color c, double factor, byte alpha) =>
        Color.FromArgb(
            alpha,
            (byte)Math.Clamp((int)Math.Round(c.R * factor), 10, 235),
            (byte)Math.Clamp((int)Math.Round(c.G * factor), 10, 235),
            (byte)Math.Clamp((int)Math.Round(c.B * factor), 10, 235));

    private static TileThemePalette Create(
        string key,
        string displayName,
        Color bgTop, Color bgBottom,
        Color hoverTop, Color hoverBottom,
        Color popupBg,
        Color primaryText, Color secondaryText,
        Color borderTop, Color borderBottom)
    {
        var bg = new LinearGradientBrush(bgTop, bgBottom, 90);
        bg.Freeze();

        var hover = new LinearGradientBrush(hoverTop, hoverBottom, 90);
        hover.Freeze();

        var popup = new SolidColorBrush(popupBg);
        popup.Freeze();

        var border = new LinearGradientBrush(borderTop, borderBottom, 90);
        border.Freeze();

        return new TileThemePalette(key, displayName, bg, hover, popup, primaryText, secondaryText, border);
    }
}