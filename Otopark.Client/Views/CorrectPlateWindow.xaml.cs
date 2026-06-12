using Otopark.Client.Helpers;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;

namespace Otopark.Client.Views;

public partial class CorrectPlateWindow : Window
{
    public string NewPlate { get; private set; } = "";

    public CorrectPlateWindow(string oldPlate, string? imagePath = null)
    {
        InitializeComponent();
        TxtOldPlate.Text = oldPlate ?? "";
        TxtNewPlate.Text = oldPlate ?? "";
        TxtNewPlate.Focus();
        TxtNewPlate.SelectAll();

        // Plakanin gorundugu arac fotografi (varsa) gosterilir.
        if (!string.IsNullOrWhiteSpace(imagePath) && System.IO.File.Exists(imagePath))
        {
            try
            {
                var bmp = new System.Windows.Media.Imaging.BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bmp.UriSource = new System.Uri(imagePath, System.UriKind.Absolute);
                bmp.EndInit();
                ImgPlate.Source = bmp;
                PhotoPanel.Visibility = Visibility.Visible;
            }
            catch { /* gorsel yuklenemezse panel gizli kalir */ }
        }
    }

    private void TxtNewPlate_PreviewTextInput(object sender, System.Windows.Input.TextCompositionEventArgs e)
    {
        // Sadece harf ve rakam kabul et
        e.Handled = !Regex.IsMatch(e.Text, "^[a-zA-Z0-9]+$");
    }

    private void TxtNewPlate_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) BtnSave_Click(sender, e);
        else if (e.Key == Key.Escape) BtnCancel_Click(sender, e);
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        var plate = new string((TxtNewPlate.Text ?? "").Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        if (!PlateRules.IsLikelyPlate(plate))
        {
            MessageBox.Show("Gecerli bir plaka giriniz (5-10 karakter, harf+rakam).",
                "Uyari", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        NewPlate = plate;
        DialogResult = true;
        Close();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
