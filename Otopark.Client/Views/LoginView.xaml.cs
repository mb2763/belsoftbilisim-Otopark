using System.Windows.Controls;
using Otopark.Core;

namespace Otopark.Client.Views;

public partial class LoginView : UserControl
{
    public LoginView()
    {
        InitializeComponent();

        // DataContext değişince bağla
        this.DataContextChanged += LoginView_DataContextChanged;
    }

    private void LoginView_DataContextChanged(object sender, System.Windows.DependencyPropertyChangedEventArgs e)
    {
        // PasswordChanged event’ini bağla
        PwdBox.PasswordChanged -= PwdBox_PasswordChanged;
        PwdBox.PasswordChanged += PwdBox_PasswordChanged;
    }

    private void PwdBox_PasswordChanged(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is LoginViewModel vm)
        {
            vm.Password = PwdBox.Password;
        }
    }
}
