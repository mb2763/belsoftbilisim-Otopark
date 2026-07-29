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

    /// <summary>
    /// Kurum logosuna CIFT TIKLAMA -> yonetici sifresi sorulur, dogruysa program kapatilir.
    /// Uygulama tam ekran (baslik cubugu yok) calistigi icin kapatmanin yoludur.
    /// </summary>
    private void KurumLogo_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2) return;   // sadece CIFT tiklama

        var dlg = new ExitPasswordWindow { Owner = System.Windows.Window.GetWindow(this) };
        if (dlg.ShowDialog() == true)
            System.Windows.Application.Current.Shutdown();
    }
}
