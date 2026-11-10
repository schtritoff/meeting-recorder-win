using System.Windows;

namespace MeetingRecorder;

/// <summary>
/// Small localized consent prompt shared by the pre-main install offer (with the opt-out checkbox) and
/// the one-time post-install pin offer (owned by the visible local main window). Labels come from the
/// shared resource catalog; the brand title stays English.
/// </summary>
public partial class StartupPromptWindow : Window
{
    internal StartupPromptWindow(string message, string primary, string secondary, string? option = null, Window? owner = null)
    {
        InitializeComponent();
        MessageText.Text = message;
        PrimaryButton.Content = primary;
        SecondaryButton.Content = secondary;
        if (option is not null)
        {
            OptionCheckBox.Content = option;
            OptionCheckBox.Visibility = Visibility.Visible;
        }
        if (owner is not null)
        {
            Owner = owner;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
    }

    internal bool OptionChecked => OptionCheckBox.IsChecked == true;

    private void OnPrimaryClick(object sender, RoutedEventArgs e) => DialogResult = true;
    private void OnSecondaryClick(object sender, RoutedEventArgs e) => DialogResult = false;

    /// <summary>Pre-main install consent: the app's first visible window, foreground by itself.</summary>
    internal static InstallAnswer AskInstall()
    {
        var prompt = new StartupPromptWindow(
            Ui.Get("InstallPromptMessage"), Ui.Get("Install"), Ui.Get("NotNow"), Ui.Get("InstallDontAsk"));
        var install = prompt.ShowDialog() == true;
        return new InstallAnswer(install, prompt.OptionChecked);
    }

    /// <summary>One-time post-install pin offer, owned by the visible local main window.</summary>
    internal static bool AskPin(Window owner)
    {
        var prompt = new StartupPromptWindow(
            Ui.Get("PinPromptMessage"), Ui.Get("PinToTaskbar"), Ui.Get("NotNow"), owner: owner);
        return prompt.ShowDialog() == true;
    }
}
