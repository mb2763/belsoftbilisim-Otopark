using System.Windows;
using System.Windows.Input;

namespace Otopark.Client.Views;

/// <summary>
/// Giris iptali icin NEDEN alan pencere. Neden ZORUNLU (sunucu da bos nedeni reddediyor).
/// Cikisi yapilmis kayitlarda ek uyari gosterir: iptal giris + cikis + odenmemis borcu
/// birlikte kapatir ve arac borc bakiyesini azaltir.
/// </summary>
public partial class CancelReasonWindow : Window
{
    /// <summary>Kullanicinin girdigi iptal nedeni (onaylanmadiysa bos).</summary>
    public string Reason { get; private set; } = "";

    public CancelReasonWindow(string plate, bool hasExit)
    {
        InitializeComponent();
        TxtPlate.Text = plate ?? "";
        WarnBox.Visibility = hasExit ? Visibility.Visible : Visibility.Collapsed;
        TxtReason.Focus();
    }

    private void TxtReason_KeyDown(object sender, KeyEventArgs e)
    {
        // Ctrl+Enter onaylar (duz Enter yeni satir acar - aciklama cok satirli olabilir)
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            BtnOnayla_Click(sender, e);
        else if (e.Key == Key.Escape)
            BtnVazgec_Click(sender, e);
    }

    private void BtnOnayla_Click(object sender, RoutedEventArgs e)
    {
        var reason = (TxtReason.Text ?? "").Trim();
        if (reason.Length < 3)
        {
            MessageBox.Show("Iptal nedeni giriniz (en az 3 karakter).",
                "Uyari", MessageBoxButton.OK, MessageBoxImage.Warning);
            TxtReason.Focus();
            return;
        }
        Reason = reason;
        DialogResult = true;
        Close();
    }

    private void BtnVazgec_Click(object sender, RoutedEventArgs e)
    {
        Reason = "";
        DialogResult = false;
        Close();
    }
}
