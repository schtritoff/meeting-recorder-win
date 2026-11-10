using System.IO;
using System.Text.Json;
using System.Windows;

namespace MeetingRecorder;

internal sealed record Settings(double? StripLeft, double? StripTop, bool DontAskInstall = false, bool EnableOpenCodeHandoff = false)
{
    /// <summary>Updates the strip position while preserving every other persisted flag (e.g. the install opt-out).</summary>
    public Settings WithStripPosition(double left, double top) => this with { StripLeft = left, StripTop = top };

    public static Settings Load()
    {
        try { return JsonSerializer.Deserialize<Settings>(File.ReadAllText(AppPaths.SettingsFile)) ?? new(null, null); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { return new(null, null); }
    }

    public void Save()
    {
        Directory.CreateDirectory(AppPaths.Root);
        var temporary = AppPaths.SettingsFile + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this));
        File.Move(temporary, AppPaths.SettingsFile, overwrite: true);
    }
}

internal static class UiMath
{
    public static Point StripPosition(double? left, double? top, double width, double height, IReadOnlyList<Rect> workAreas)
    {
        var primary = workAreas[0];
        var point = new Point(left ?? primary.Left, top ?? primary.Top);
        var area = workAreas.FirstOrDefault(area => area.Contains(point), primary);
        return new Point(ClampPosition(point.X, area.Left, area.Width, width),
            ClampPosition(point.Y, area.Top, area.Height, height));
    }

    // Offset change for a wheel delta; lines is SystemParameters.WheelScrollLines, which is -1 in page-scroll mode.
    public static double WheelStep(int delta, int lines) => -delta / 120.0 * Math.Max(1, lines) * 16;

    public static double Meter(double peak) => Math.Clamp((20 * Math.Log10(Math.Max(peak, 1e-6)) + 60) / 60, 0, 1);
    public static double ClampPosition(double position, double origin, double size, double windowSize) =>
        Math.Clamp(double.IsFinite(position) ? position : origin, origin, Math.Max(origin, origin + size - windowSize));

    /// <summary>Converts a physical-pixel rectangle (GetMonitorInfo) to WPF DIPs; scale is the monitor DPI / 96.</summary>
    public static Rect PhysicalRectToDips(Rect physical, double scale)
    {
        if (!double.IsFinite(scale) || scale <= 0) scale = 1; // a broken DPI read must not collapse the area
        return new Rect(physical.Left / scale, physical.Top / scale, physical.Width / scale, physical.Height / scale);
    }
}
