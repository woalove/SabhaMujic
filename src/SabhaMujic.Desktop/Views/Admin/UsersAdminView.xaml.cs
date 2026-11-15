using System.Windows;
using System.Windows.Controls;
using SabhaMujic.Desktop.ViewModels.Admin;

namespace SabhaMujic.Desktop.Views.Admin;

public partial class UsersAdminView : UserControl
{
    private UsersAdminViewModel? _vm;

    public UsersAdminView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (_vm is not null)
            {
                _vm.SecretCleared -= ClearSecret;
            }

            _vm = e.NewValue as UsersAdminViewModel;
            if (_vm is not null)
            {
                _vm.SecretCleared += ClearSecret;
            }
        };
    }

    // PasswordBox.Password is not bindable (by design).
    private void OnNewSecretChanged(object sender, RoutedEventArgs e)
    {
        if (_vm is not null)
        {
            _vm.NewUserSecret = NewSecretBox.Password;
        }
    }

    private void ClearSecret()
    {
        if (NewSecretBox.Password.Length > 0)
        {
            NewSecretBox.Clear();
        }
    }
}
