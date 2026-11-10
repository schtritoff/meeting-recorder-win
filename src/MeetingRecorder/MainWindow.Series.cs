using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using MeetingRecorder.Recording;
using Microsoft.Win32;

namespace MeetingRecorder;

public partial class MainWindow
{
    private MeetingSeries? selectedSeries;

    private void SelectSeries(MeetingSeries? series)
    {
        selectedSeries = series;
        TitleBox.IsReadOnly = series is not null;
        TitleBox.Text = series?.Title ?? "";
        var caption = Ui.Get(series is null ? "ContinuePrevious" : "StartNewMeeting");
        ContinueButton.Content = caption;
        AutomationProperties.SetName(ContinueButton, caption);
    }

    private void ContinueClick(object sender, RoutedEventArgs e)
    {
        if (processing || recorder?.IsRecording == true) return;
        CancelOpenCode(); // Continuing a previous meeting leaves the Done result; its Continue in OpenCode is abandoned.
        if (selectedSeries is not null) { SelectSeries(null); return; }
        var menu = new ContextMenu { PlacementTarget = ContinueButton };
        AddItem(Ui.Get("NewRecording"), null, () => SelectSeries(null));
        foreach (var series in MeetingSeries.Recent(AppPaths.MeetingsOutput))
            AddItem(series.Title, series.Folder, () => ChooseSeries(series.Folder));
        AddItem(Ui.Get("BrowseMeeting"), null, () =>
        {
            var dialog = new OpenFolderDialog { Title = Ui.Get("BrowseMeeting") };
            if (dialog.ShowDialog(this) == true) ChooseSeries(dialog.FolderName);
        });
        menu.IsOpen = true;

        void AddItem(string label, string? tooltip, Action action)
        {
            var item = new MenuItem { Header = label, ToolTip = tooltip };
            AutomationProperties.SetName(item, label);
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
    }

    private void ChooseSeries(string folder)
    {
        try
        {
            var series = MeetingSeries.Read(folder);
            series.CheckWritable();
            SelectSeries(series);
        }
        catch (Exception ex) { App.ShowError("ContinueSelectionError", ex); }
    }
}
