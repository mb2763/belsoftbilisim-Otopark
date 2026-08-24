using System.Windows;
using System.Windows.Input;

namespace Otopark.Client.Views;

/// <summary>
/// MISAFIR ARAC isareti icin ACIKLAMA alan pencere (24.08.2026).
///
/// Aciklama ZORUNLU (sunucu da 3 karakterden kisa aciklamayi reddediyor).
/// Kalibi CancelReasonWindow ile aynidir; ayri pencere olmasinin sebebi metinlerin
/// ve renklerin "iptal" ile karistirilmamasi: bu islem hicbir kaydi silmez ve
/// ucret/borc akisina dokunmaz.
/// </summary>
public partial class GuestNoteWindow : Window
{
    /// <summary>Kullanicinin girdigi aciklama (onaylanmadiysa bos).</summary>
    public string Note { get; private set; } = "";

    public GuestNoteWindow(string plate)
    {
        InitializeComponent();
        TxtPlate.Text = plate ?? "";
        TxtNote.Focus();
    }

    private void TxtNote_KeyDown(object sender, KeyEventArgs e)
    {
        // Ctrl+Enter onaylar (duz Enter yeni satir acar - aciklama cok satirli olabilir)
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            BtnOnayla_Click(sender, e);
        else if (e.Key == Key.Escape)
            BtnVazgec_Click(sender, e);
    }

    private void BtnOnayla_Click(object sender, RoutedEventArgs e)
    {
        var note = (TxtNote.Text ?? "").Trim();
        if (note.Length < 3)
        {
            MessageBox.Show("Aciklama giriniz (en az 3 karakter).",
                "Uyari", MessageBoxButton.OK, MessageBoxImage.Warning);
            TxtNote.Focus();
            return;
        }
        Note = note;
        DialogResult = true;
        Close();
    }

    private void BtnVazgec_Click(object sender, RoutedEventArgs e)
    {
        Note = "";
        DialogResult = false;
        Close();
    }
}
