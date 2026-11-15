using System.Windows;
using System.Windows.Controls;
using SabhaMujic.Desktop.ViewModels;

namespace SabhaMujic.Desktop.Views;

public partial class LoginView : UserControl
{
    private LoginViewModel? _vm;

    public LoginView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                Dispatcher.BeginInvoke(() => UserNameBox.Focus());
            }
        };
    }

    // PasswordBox.Password is not bindable (by design); push it to the view model on change.
    private void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_vm is not null)
        {
            _vm.Secret = SecretBox.Password;
        }
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm is not null)
        {
            _vm.SecretCleared -= ClearSecret;
        }

        _vm = e.NewValue as LoginViewModel;
        if (_vm is not null)
        {
            _vm.SecretCleared += ClearSecret;
        }
    }

    private void ClearSecret()
    {
        if (SecretBox.Password.Length > 0)
        {
            SecretBox.Clear();
        }
    }
}
