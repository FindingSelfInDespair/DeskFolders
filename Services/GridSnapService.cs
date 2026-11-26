using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using DeskFolders.Models;
using Drawing = System.Drawing;
using WinForms = System.Windows.Forms;

namespace DeskFolders.Services;

public static class GridSnapService
{
    public const double MarginX = 14;
    public const double MarginY = 12;
    public const double MinGapX = 10;
    public const double MinGapY = 10;

    public static (double WinWidth, double WinHeight, double BoxSize, double CornerRadius, double FontSize) GetMetrics(
        TileSizePreset preset,
        bool showLabels = true)
    {
        var baseMetrics = preset switch
        {
            TileSizePreset.Compact => (W: 100.0, H: 114.0, Box: 76.0, R: 17.0, Font: 11.5),
            TileSizePreset.Large => (W: 128.0, H: 144.0, Box: 104.0, R: 24.0, Font: 12.5),
            _ => (W: 112.0, H: 128.0, Box: 88.0, R: 20.0, Font: 12.0)
        };

        double height = showLabels ? baseMetrics.H : baseMetrics.Box + 20;
        return (baseMetrics.W, height, baseMetrics.Box, baseMetrics.R, baseMetrics.Font);
    }

    public static Point FindNearestFreeCell(
        double rawX,
        double rawY,
        double dpiScaleX,
        double dpiScaleY,
        TileSizePreset preset,
        bool showLabels,
        IEnumerable<FolderTileConfig> allTiles,
        FolderTileConfig? ignoreTile = null)
    {
        var (winW, winH, _, _, _) = GetMetrics(preset, showLabels);

        var centerPx = new Drawing.Point(
            (int)((rawX + winW / 2) * dpiScaleX),
            (int)((rawY + winH / 2) * dpiScaleY));

        var workPx = WinForms.Screen.FromPoint(centerPx).WorkingArea;
        double workLeft = workPx.Left / dpiScaleX;
        double workTop = workPx.Top / dpiScaleY;
        double workWidth = workPx.Width / dpiScaleX;
        double workHeight = workPx.Height / dpiScaleY;

        double availW = Math.Max(winW, workWidth - MarginX * 2);
        double availH = Math.Max(winH, workHeight - MarginY * 2);

        int cols = Math.Max(1, (int)Math.Floor((availW + MinGapX) / (winW + MinGapX)));
        int rows = Math.Max(1, (int)Math.Floor((availH + MinGapY) / (winH + MinGapY)));

        double stepX = cols > 1 ? (availW - winW) / (cols - 1) : 0;
        double stepY = rows > 1 ? (availH - winH) / (rows - 1) : 0;

        int desiredCol = cols > 1
            ? Math.Clamp((int)Math.Round((rawX - (workLeft + MarginX)) / stepX), 0, cols - 1)
            : 0;
        int desiredRow = rows > 1
            ? Math.Clamp((int)Math.Round((rawY - (workTop + MarginY)) / stepY), 0, rows - 1)
            : 0;

        var occupied = allTiles
            .Where(t => t != ignoreTile)
            .Select(t => new Point(t.X, t.Y))
            .ToList();

        double tolX = Math.Max(winW, stepX) * 0.45;
        double tolY = Math.Max(winH, stepY) * 0.45;

        Point bestPoint = CellToPoint(workLeft, workTop, desiredCol, desiredRow, stepX, stepY);
        if (!IsOccupied(bestPoint, occupied, tolX, tolY))
            return bestPoint;

        double bestDist = double.MaxValue;
        for (int c = 0; c < cols; c++)
        {
            for (int r = 0; r < rows; r++)
            {
                var pt = CellToPoint(workLeft, workTop, c, r, stepX, stepY);
                if (IsOccupied(pt, occupied, tolX, tolY))
                    continue;

                double dist = Math.Pow(pt.X - rawX, 2) + Math.Pow(pt.Y - rawY, 2);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    bestPoint = pt;
                }
            }
        }

        return bestPoint;
    }

    public static Point ApplyMagneticAlignment(
        double rawX,
        double rawY,
        double dpiScaleX,
        double dpiScaleY,
        TileSizePreset preset,
        bool showLabels,
        IEnumerable<FolderTileConfig> allTiles,
        FolderTileConfig? ignoreTile)
    {
        const double snapThreshold = 10;
        var (winW, winH, _, _, _) = GetMetrics(preset, showLabels);

        double snappedX = rawX;
        double snappedY = rawY;
        double bestDx = snapThreshold;
        double bestDy = snapThreshold;

        foreach (var other in allTiles)
        {
            if (other == ignoreTile)
                continue;

            double dx = Math.Abs(rawX - other.X);
            if (dx < bestDx)
            {
                bestDx = dx;
                snappedX = other.X;
            }

            double dy = Math.Abs(rawY - other.Y);
            if (dy < bestDy)
            {
                bestDy = dy;
                snappedY = other.Y;
            }
        }

        var centerPx = new Drawing.Point(
            (int)((snappedX + winW / 2) * dpiScaleX),
            (int)((snappedY + winH / 2) * dpiScaleY));

        var workPx = WinForms.Screen.FromPoint(centerPx).WorkingArea;
        double minX = workPx.Left / dpiScaleX + 6;
        double minY = workPx.Top / dpiScaleY + 6;
        double maxX = workPx.Right / dpiScaleX - winW - 6;
        double maxY = workPx.Bottom / dpiScaleY - winH - 6;

        return new Point(
            Math.Clamp(snappedX, minX, Math.Max(minX, maxX)),
            Math.Clamp(snappedY, minY, Math.Max(minY, maxY)));
    }

    public static Point? EnsureVisibleOnScreens(
        double x,
        double y,
        double dpiScaleX,
        double dpiScaleY,
        TileSizePreset preset,
        bool showLabels,
        bool snapToGrid,
        IEnumerable<FolderTileConfig> allTiles,
        FolderTileConfig ignoreTile)
    {
        var (winW, winH, _, _, _) = GetMetrics(preset, showLabels);
        var centerPx = new Drawing.Point(
            (int)((x + winW / 2) * dpiScaleX),
            (int)((y + winH / 2) * dpiScaleY));

        foreach (var screen in WinForms.Screen.AllScreens)
        {
            if (screen.WorkingArea.Contains(centerPx))
                return null;
        }

        var primaryWork = WinForms.Screen.PrimaryScreen?.WorkingArea ?? WinForms.Screen.AllScreens[0].WorkingArea;
        double pLeft = primaryWork.Left / dpiScaleX;
        double pTop = primaryWork.Top / dpiScaleY;
        double pRight = primaryWork.Right / dpiScaleX;
        double pBottom = primaryWork.Bottom / dpiScaleY;

        double clampedX = Math.Clamp(x, pLeft + 8, Math.Max(pLeft + 8, pRight - winW - 8));
        double clampedY = Math.Clamp(y, pTop + 8, Math.Max(pTop + 8, pBottom - winH - 8));

        if (snapToGrid)
            return FindNearestFreeCell(clampedX, clampedY, dpiScaleX, dpiScaleY, preset, showLabels, allTiles, ignoreTile);

        return new Point(clampedX, clampedY);
    }

    private static Point CellToPoint(double workLeft, double workTop, int col, int row, double stepX, double stepY) =>
        new(Math.Round(workLeft + MarginX + col * stepX), Math.Round(workTop + MarginY + row * stepY));

    private static bool IsOccupied(Point pt, List<Point> occupied, double tolX, double tolY)
    {
        foreach (var o in occupied)
        {
            if (Math.Abs(o.X - pt.X) < tolX && Math.Abs(o.Y - pt.Y) < tolY)
                return true;
        }
        return false;
    }
}