using System.Windows;
using System.Windows.Controls;
using SabhaMujic.Desktop.ViewModels;

namespace SabhaMujic.Desktop.Views;

public partial class SetupView : UserControl
{
    private SetupViewModel? _vm;

    public SetupView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                Dispatcher.BeginInvoke(() => AdminNameBox.Focus());
            }
        };
    }

    // PasswordBox.Password is not bindable (by design); push it to the view model on change.
    private void OnSecretChanged(object sender, RoutedEventArgs e)
    {
        if (_vm is not null)
        {
            _vm.Secret = SecretBox.Password;
        }
    }

    private void OnConfirmChanged(object sender, RoutedEventArgs e)
    {
        if (_vm is not null)
        {
            _vm.ConfirmSecret = ConfirmBox.Password;
        }
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm is not null)
        {
            _vm.SecretsCleared -= ClearSecrets;
        }

        _vm = e.NewValue as SetupViewModel;
        if (_vm is not null)
        {
            _vm.SecretsCleared += ClearSecrets;
        }
    }

    private void ClearSecrets()
    {
        SecretBox.Clear();
        ConfirmBox.Clear();
    }
}
