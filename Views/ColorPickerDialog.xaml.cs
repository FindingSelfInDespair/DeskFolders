using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DeskFolders.Services;

namespace DeskFolders.Views;

public partial class ColorPickerDialog : Window
{
    private double _hue = 210;
    private double _sat = 0.75;
    private double _val = 0.90;
    private bool _draggingSv;
    private bool _draggingHue;
    private bool _suppressHexEvent;

    public string SelectedHex { get; private set; } = "#3B82F6";

    public ColorPickerDialog(string? initialTint)
    {
        InitializeComponent();

        if (TileThemeService.TryParseHex(initialTint, out var c))
            RgbToHsv(c, out _hue, out _sat, out _val);

        Loaded += (_, _) => UpdateUiFromHsv(updateHex: true);
    }

    private void OnSvMouseDown(object sender, MouseButtonEventArgs e)
    {
        _draggingSv = true;
        SvCanvas.CaptureMouse();
        UpdateSvFromPoint(e.GetPosition(SvCanvas));
    }

    private void OnSvMouseMove(object sender, MouseEventArgs e)
    {
        if (_draggingSv)
            UpdateSvFromPoint(e.GetPosition(SvCanvas));
    }

    private void OnSvMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_draggingSv)
        {
            _draggingSv = false;
            SvCanvas.ReleaseMouseCapture();
        }
    }

    private void UpdateSvFromPoint(Point p)
    {
        double w = Math.Max(1, SvCanvas.ActualWidth);
        double h = Math.Max(1, SvCanvas.ActualHeight);
        _sat = Math.Clamp(p.X / w, 0, 1);
        _val = Math.Clamp(1.0 - p.Y / h, 0, 1);
        UpdateUiFromHsv(updateHex: true);
    }

    private void OnHueMouseDown(object sender, MouseButtonEventArgs e)
    {
        _draggingHue = true;
        HueBar.CaptureMouse();
        UpdateHueFromPoint(e.GetPosition(HueBar));
    }

    private void OnHueMouseMove(object sender, MouseEventArgs e)
    {
        if (_draggingHue)
            UpdateHueFromPoint(e.GetPosition(HueBar));
    }

    private void OnHueMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_draggingHue)
        {
            _draggingHue = false;
            HueBar.ReleaseMouseCapture();
        }
    }

    private void UpdateHueFromPoint(Point p)
    {
        double w = Math.Max(1, HueBar.ActualWidth);
        _hue = Math.Clamp(p.X / w, 0, 0.9999) * 360.0;
        UpdateUiFromHsv(updateHex: true);
    }

    private void OnHexTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressHexEvent)
            return;

        if (TileThemeService.TryParseHex(HexBox.Text, out var parsed))
        {
            RgbToHsv(parsed, out _hue, out _sat, out _val);
            UpdateUiFromHsv(updateHex: false);
        }
    }

    private void UpdateUiFromHsv(bool updateHex)
    {
        Color pureHue = HsvToRgb(_hue, 1, 1);
        HueBackdrop.Fill = new SolidColorBrush(pureHue);

        Color current = HsvToRgb(_hue, _sat, _val);
        SelectedHex = $"#{current.R:X2}{current.G:X2}{current.B:X2}";

        var palette = TileThemeService.CreateFromColor(current, SelectedHex);
        PreviewTile.Background = palette.TileBackground;
        PreviewTile.BorderBrush = palette.BorderBrush;

        if (SvCanvas.ActualWidth > 0)
        {
            Canvas.SetLeft(SvThumb, _sat * SvCanvas.ActualWidth - SvThumb.Width / 2);
            Canvas.SetTop(SvThumb, (1.0 - _val) * SvCanvas.ActualHeight - SvThumb.Height / 2);
        }

        if (HueBar.ActualWidth > 0)
            Canvas.SetLeft(HueThumb, Math.Clamp(_hue / 360.0 * HueBar.ActualWidth - HueThumb.Width / 2, 0, HueBar.ActualWidth - HueThumb.Width));

        if (updateHex)
        {
            _suppressHexEvent = true;
            HexBox.Text = SelectedHex;
            _suppressHexEvent = false;
        }
    }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private static Color HsvToRgb(double h, double s, double v)
    {
        double c = v * s;
        double x = c * (1 - Math.Abs((h / 60.0) % 2 - 1));
        double m = v - c;

        (double r1, double g1, double b1) = h switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x)
        };

        return Color.FromRgb(
            (byte)Math.Round((r1 + m) * 255),
            (byte)Math.Round((g1 + m) * 255),
            (byte)Math.Round((b1 + m) * 255));
    }

    private static void RgbToHsv(Color c, out double h, out double s, out double v)
    {
        double r = c.R / 255.0;
        double g = c.G / 255.0;
        double b = c.B / 255.0;

        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double delta = max - min;

        h = 0;
        if (delta > 0.0001)
        {
            if (Math.Abs(max - r) < 0.0001)
                h = 60 * (((g - b) / delta) % 6);
            else if (Math.Abs(max - g) < 0.0001)
                h = 60 * (((b - r) / delta) + 2);
            else
                h = 60 * (((r - g) / delta) + 4);

            if (h < 0)
                h += 360;
        }

        s = max <= 0.0001 ? 0 : delta / max;
        v = max;
    }
}