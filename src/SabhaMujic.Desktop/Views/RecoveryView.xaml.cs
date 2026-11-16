using System.Windows;
using System.Windows.Controls;
using SabhaMujic.Desktop.ViewModels;

namespace SabhaMujic.Desktop.Views;

public partial class RecoveryView : UserControl
{
    private RecoveryViewModel? _vm;

    public RecoveryView()
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

        _vm = e.NewValue as RecoveryViewModel;
        if (_vm is not null)
        {
            _vm.SecretsCleared += ClearSecrets;
        }
    }

    private void ClearSecrets()
    {
        if (SecretBox.Password.Length > 0)
        {
            SecretBox.Clear();
        }

        if (ConfirmBox.Password.Length > 0)
        {
            ConfirmBox.Clear();
        }
    }
}
