using System.Windows;
using System.Windows.Input;

namespace Otopark.Client.Views;

/// <summary>
/// KARA LISTEYE EKLEME icin NEDEN alan pencere (11.10.2026). Kalibi GuestNoteWindow ile aynidir.
/// Neden ZORUNLU; kara liste kaydinda ve web panelindeki "Kara Liste" ekraninda gorunur.
/// </summary>
public partial class KaraListeWindow : Window
{
    /// <summary>Kullanicinin girdigi neden (onaylanmadiysa bos).</summary>
    public string Note { get; private set; } = "";

    public KaraListeWindow(string plate, string varsayilanNeden = "")
    {
        InitializeComponent();
        TxtPlate.Text = plate ?? "";
        TxtNote.Text = varsayilanNeden ?? "";
        TxtNote.Focus();
        TxtNote.SelectAll();
    }

    private void TxtNote_KeyDown(object sender, KeyEventArgs e)
    {
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
            MessageBox.Show("Neden giriniz (en az 3 karakter).",
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
