using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace MeetingRecorder;

public partial class StripWindow : Window
{
    private readonly MainWindow main;
    private bool exiting;
    internal StripWindow(MainWindow main)
    {
        InitializeComponent();
        this.main = main;
        SourceInitialized += (_, _) => PlaceOnMonitor();
        Closing += (_, e) =>
        {
            if (exiting) return;
            e.Cancel = true;
            main.RestoreMain();
        };
    }

    internal void CloseForExit() { exiting = true; Close(); }

    private void PlaceOnMonitor()
    {
        var settings = Settings.Load();
        var source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)!;
        var fromDevice = source.CompositionTarget.TransformFromDevice;
        var areas = new List<Rect>();
        MonitorEnum callback = (nint monitor, nint dc, ref NativeRect bounds, nint data) =>
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (GetMonitorInfo(monitor, ref info))
            {
                var area = new Rect(fromDevice.Transform(new Point(info.Work.Left, info.Work.Top)),
                    fromDevice.Transform(new Point(info.Work.Right, info.Work.Bottom)));
                if ((info.Flags & 1) != 0) areas.Insert(0, area); else areas.Add(area);
            }
            return true;
        };
        EnumDisplayMonitors(0, 0, callback, 0);
        if (areas.Count == 0) areas.Add(SystemParameters.WorkArea);
        var position = UiMath.StripPosition(settings.StripLeft, settings.StripTop, Width, Height, areas);
        Left = position.X;
        Top = position.Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
    private delegate bool MonitorEnum(nint monitor, nint dc, ref NativeRect bounds, nint data);
    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(nint dc, nint clip, MonitorEnum callback, nint data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);

    internal void Update(string elapsed, double mic, double system, bool paused)
    {
        TimeText.Text = elapsed;
        MicMeter.Value = mic;
        SystemMeter.Value = system;
        PauseButton.Content = paused ? "\u25b6" : "\u23f8";
        PauseButton.ToolTip = Ui.Get(paused ? "Resume" : "Pause");
        System.Windows.Automation.AutomationProperties.SetName(PauseButton, (string)PauseButton.ToolTip);
    }

    private void DragStrip(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject target && FindButton(target)) return;
        DragMove();
        try { Settings.Load().WithStripPosition(Left, Top).Save(); }
        catch (Exception ex) { App.ShowError("PositionError", ex); }
    }

    private static bool FindButton(DependencyObject target)
    {
        for (DependencyObject? current = target; current is not null;
             current = current is System.Windows.Media.Visual ? System.Windows.Media.VisualTreeHelper.GetParent(current) : (current as FrameworkContentElement)?.Parent)
            if (current is ButtonBase) return true;
        return false;
    }

    private void PauseClick(object sender, RoutedEventArgs e) => main.TogglePause();
    private void StopClick(object sender, RoutedEventArgs e) => main.StopRecording();
    private void RestoreClick(object sender, RoutedEventArgs e) => main.RestoreMain();
    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.M || Keyboard.Modifiers != ModifierKeys.Control) return;
        e.Handled = true;
        main.RestoreMain();
    }
}
