using System.Globalization;
using System.Resources;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Markup;

namespace MeetingRecorder;

public static class Ui
{
    private static readonly ResourceManager resources = new("MeetingRecorder.Strings", typeof(Ui).Assembly);
    // English until App selects the geographic region; constructing a window in a test is deterministic.
    public static CultureInfo Culture { get; private set; } = CultureInfo.GetCultureInfo("en");

    internal static CultureInfo SelectCulture(Func<string?> detectRegion)
    {
        try
        {
            if (string.Equals(detectRegion(), "HR", StringComparison.OrdinalIgnoreCase))
                return CultureInfo.GetCultureInfo("hr");
        }
        catch (Exception) { /* Unknown/unavailable geographic settings use English. */ }
        return CultureInfo.GetCultureInfo("en");
    }

    internal static void Initialize() => Culture = SelectCulture(GetWindowsRegion);

    internal static string? GetWindowsRegion()
    {
        var name = new StringBuilder(85);
        return GetUserDefaultGeoName(name, name.Capacity) > 0 ? name.ToString() : null;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetUserDefaultGeoName(StringBuilder geoName, int geoNameCount);

    public static string Get(string key) => Get(key, Culture);
    internal static string Get(string key, CultureInfo culture) =>
        resources.GetString(key, culture) ?? throw new MissingManifestResourceException($"Missing UI resource: {key}");

    public static string Format(string key, params object[] args) => Format(key, Culture, args);
    internal static string Format(string key, CultureInfo culture, params object[] args) =>
        string.Format(culture, Get(key, culture), args);
}

public sealed class LocExtension(string key) : MarkupExtension
{
    public override object ProvideValue(IServiceProvider serviceProvider) => Ui.Get(key);
}
