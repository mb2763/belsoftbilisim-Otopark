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

        // Pencere ekrandan TASMASIN: dusuk cozunurlukte Kaydet/Kapat butonlari
        // ekranin altinda kalip gorunmez oluyordu. Yukseklik calisma alanina gore sinirlanir,
        // fotograf da (yildiz satir) kalan alana gore kuculur.
        double calismaY = SystemParameters.WorkArea.Height;
        double calismaX = SystemParameters.WorkArea.Width;
        MaxHeight = calismaY;
        MaxWidth = calismaX;
        Height = System.Math.Min(880, calismaY - 40);
        if (Width > calismaX - 40) Width = calismaX - 40;

        TxtOldPlate.Text = oldPlate ?? "";
        TxtNewPlate.Text = oldPlate ?? "";
        TxtNewPlate.Focus();
        TxtNewPlate.SelectAll();

        // Plakanin gorundugu arac fotografi (varsa) gosterilir.
        // Yerel dosya yolu ya da http(s) adresi kabul edilir.
        bool isUrl = !string.IsNullOrWhiteSpace(imagePath) &&
                     (imagePath!.StartsWith("http://", System.StringComparison.OrdinalIgnoreCase) ||
                      imagePath!.StartsWith("https://", System.StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(imagePath) && (isUrl || System.IO.File.Exists(imagePath)))
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
                ZoomBar.Visibility = Visibility.Visible;
            }
            catch { /* gorsel yuklenemezse panel gizli kalir */ }
        }

        // Fotograf yoksa pencereyi gereksiz uzun tutma
        if (PhotoPanel.Visibility != Visibility.Visible)
        {
            SizeToContent = SizeToContent.Height;
        }
    }

    // =====================================================================
    // FOTOGRAF YAKINLASTIRMA (zoom + pan) — plakanin net okunmasi icin.
    // LayoutTransform kullanilir: ScrollViewer olceklenmis boyutu gorur,
    // boylece kaydirma cubuklari dogru calisir.
    // =====================================================================
    private const double ZOOM_MIN = 1.0;
    private const double ZOOM_MAX = 6.0;
    private bool _panning;
    private Point _panStart;
    private double _panHOff, _panVOff;

    private void ApplyZoom(double scale)
    {
        double s = scale < ZOOM_MIN ? ZOOM_MIN : (scale > ZOOM_MAX ? ZOOM_MAX : scale);
        ImgScale.ScaleX = s;
        ImgScale.ScaleY = s;
        LblZoom.Text = "%" + (int)System.Math.Round(s * 100);
    }

    private void BtnZoomIn_Click(object sender, RoutedEventArgs e) => ApplyZoom(ImgScale.ScaleX + 0.5);
    private void BtnZoomOut_Click(object sender, RoutedEventArgs e) => ApplyZoom(ImgScale.ScaleX - 0.5);
    private void BtnZoomReset_Click(object sender, RoutedEventArgs e)
    {
        ApplyZoom(1.0);
        ImgScroll.ScrollToHorizontalOffset(0);
        ImgScroll.ScrollToVerticalOffset(0);
    }

    private void ImgScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        ApplyZoom(ImgScale.ScaleX + (e.Delta > 0 ? 0.3 : -0.3));
        e.Handled = true;   // ScrollViewer'in dikey kaydirmasini engelle
    }

    private void ImgScroll_DoubleClick(object sender, MouseButtonEventArgs e) => BtnZoomReset_Click(sender, e);

    private void ImgScroll_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (ImgScale.ScaleX <= ZOOM_MIN) return;    // 1x'te gezdirmeye gerek yok
        _panning = true;
        _panStart = e.GetPosition(ImgScroll);
        _panHOff = ImgScroll.HorizontalOffset;
        _panVOff = ImgScroll.VerticalOffset;
        ImgScroll.CaptureMouse();
        ImgScroll.Cursor = Cursors.ScrollAll;
    }

    private void ImgScroll_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_panning) return;
        var p = e.GetPosition(ImgScroll);
        ImgScroll.ScrollToHorizontalOffset(_panHOff - (p.X - _panStart.X));
        ImgScroll.ScrollToVerticalOffset(_panVOff - (p.Y - _panStart.Y));
    }

    private void ImgScroll_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_panning) return;
        _panning = false;
        ImgScroll.ReleaseMouseCapture();
        ImgScroll.Cursor = Cursors.Hand;
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
        // Personel gozuyle gordugu plakayi yaziyor -> gevsek dogrulama.
        // IsLikelyPlate kullanilirsa yabanci plakalar (AKKUS-H gibi) reddedilir.
        if (!PlateRules.IsAcceptableManualPlate(plate))
        {
            MessageBox.Show("Gecerli bir plaka giriniz (4-12 karakter, harf ve rakam).",
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
