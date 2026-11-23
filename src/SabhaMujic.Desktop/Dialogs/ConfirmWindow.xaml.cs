using System.Windows;
using SabhaMujic.Desktop.Services;

namespace SabhaMujic.Desktop.Dialogs;

/// <summary>
/// Yes/no question (or, with no cancel text, a plain message) in the app's own style, replacing the Windows message box.
/// Destructive confirmations ("Delete", "Forget", …) get the filled danger button: the confirm step.
/// </summary>
public partial class ConfirmWindow : Window
{
    public ConfirmWindow(string title, string message, string confirmText, string? cancelText = "Cancel")
    {
        InitializeComponent();
        DialogChrome.Attach(this);
        Title = title;
        MessageText.Text = message;
        ConfirmButton.Content = confirmText;
        if (DialogChrome.IsDestructive(confirmText))
        {
            ConfirmButton.Style = (Style)FindResource("DestructiveFilledButton");
        }

        if (cancelText is null)
        {
            CancelButton.Visibility = Visibility.Collapsed;
            ConfirmButton.IsCancel = true;
        }
        else
        {
            CancelButton.Content = cancelText;
        }

        // Destructive questions start with Cancel focused, so a stray Enter doesn't delete anything.
        var safeDefault = cancelText is not null && DialogChrome.IsDestructive(confirmText);
        Loaded += (_, _) => (safeDefault ? CancelButton : ConfirmButton).Focus();
    }

    private void OnConfirm(object sender, RoutedEventArgs e) => DialogResult = true;
}
