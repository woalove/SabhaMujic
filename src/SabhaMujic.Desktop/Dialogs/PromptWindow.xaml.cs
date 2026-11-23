using System.Windows;
using System.Windows.Automation;
using SabhaMujic.Desktop.Services;

namespace SabhaMujic.Desktop.Dialogs;

/// <summary>Single-line text prompt, or a secret prompt with confirmation (password reset).</summary>
public partial class PromptWindow : Window
{
    private readonly bool _isSecret;

    public PromptWindow(string title, string label, string initialValue, bool isSecret)
    {
        InitializeComponent();
        DialogChrome.Attach(this);
        Title = title;
        LabelText.Text = label;
        AutomationProperties.SetName(ValueBox, label);
        AutomationProperties.SetName(SecretBox, label);
        _isSecret = isSecret;
        if (isSecret)
        {
            ValueBox.Visibility = Visibility.Collapsed;
            SecretBox.Visibility = ConfirmLabel.Visibility = ConfirmBox.Visibility = Visibility.Visible;
        }
        else
        {
            ValueBox.Text = initialValue;
        }

        Loaded += (_, _) =>
        {
            if (_isSecret)
            {
                SecretBox.Focus();
            }
            else
            {
                ValueBox.Focus();
                ValueBox.SelectAll();
            }

            Validate();
        };
    }

    public string? Value { get; private set; }

    private void OnInputChanged(object sender, RoutedEventArgs e) => Validate();

    private void Validate()
    {
        if (_isSecret)
        {
            var mismatch = ConfirmBox.Password.Length > 0 && ConfirmBox.Password != SecretBox.Password;
            MismatchText.Visibility = mismatch ? Visibility.Visible : Visibility.Collapsed;
            OkButton.IsEnabled = SecretBox.Password.Length > 0 && SecretBox.Password == ConfirmBox.Password;
        }
        else
        {
            OkButton.IsEnabled = !string.IsNullOrWhiteSpace(ValueBox.Text);
        }
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        Value = _isSecret ? SecretBox.Password : ValueBox.Text.Trim();
        DialogResult = true;
    }
}
