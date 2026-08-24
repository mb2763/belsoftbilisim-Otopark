using Otopark.Api.Services;
using Otopark.Core.Session;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Otopark.Client.Views
{
    public partial class WashWindow : Window
    {
        private readonly VehicleParkApiService _api;
        public ObservableCollection<WashRow> Rows { get; } = new();
        private WashRow? _selected;

        public WashWindow(VehicleParkApiService api)
        {
            InitializeComponent();
            _api = api;
            EntryList.ItemsSource = Rows;
            Loaded += async (_, __) => await LoadAsync();
        }

        private async System.Threading.Tasks.Task LoadAsync()
        {
            try
            {
                Rows.Clear();
                _selected = null;
                SelectedPlateText.Text = "—";
                SelectedInfoText.Text = "Sağdaki listeden bir araç seçiniz.";
                PrintBtn.IsEnabled = false;
                ResultBox.Visibility = Visibility.Collapsed;

                var list = await _api.GetWashRecentEntriesAsync(UserSession.CompanyId, 15);
                foreach (var e in list)
                {
                    Rows.Add(new WashRow
                    {
                        EntryId = e.EntryId,
                        Plate = e.Plate ?? "",
                        MinutesIn = e.MinutesIn,
                        EntryTime = e.EntryTime,
                        AlreadyWashed = e.AlreadyWashed,
                        RemainingMinutes = e.RemainingMinutes
                    });
                }
                if (Rows.Count == 0)
                    SelectedInfoText.Text = "Otoparkta araç bulunmuyor.";
            }
            catch (Exception ex)
            {
                MessageBox.Show("Liste alınamadı: " + ex.Message, "Hata", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private async void RefreshBtn_Click(object sender, RoutedEventArgs e) => await LoadAsync();

        private void EntryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _selected = EntryList.SelectedItem as WashRow;
            ResultBox.Visibility = Visibility.Collapsed;

            if (_selected == null)
            {
                SelectedPlateText.Text = "—";
                SelectedInfoText.Text = "Sağdaki listeden bir araç seçiniz.";
                PrintBtn.IsEnabled = false;
                return;
            }

            SelectedPlateText.Text = _selected.Plate;
            SelectedInfoText.Text = $"{_selected.MinutesIn} dakikadır otoparkta. " +
                (_selected.AlreadyWashed
                    ? "⚠ Bu araca zaten yıkama fişi basılmış (araç çıkmadan tekrar basılamaz)."
                    : "Fiş basmak için aşağıdaki butona basınız.");
            PrintBtn.IsEnabled = !_selected.AlreadyWashed;
        }

        private async void PrintBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_selected == null) return;
            if (_selected.AlreadyWashed)
            {
                MessageBox.Show("Bu araca zaten yıkama fişi basılmış. Araç çıkmadan tekrar basılamaz.",
                    "Uyarı", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            PrintBtn.IsEnabled = false;
            try
            {
                // Serbest sure SUNUCUDA tanimli (WASH_SETTING); buradan gonderilen deger yok sayilir (0 gec).
                var res = await _api.PrintWashReceiptAsync(
                    UserSession.CompanyId, UserSession.UserId, _selected.EntryId, 0);

                if (!res.Success)
                {
                    MessageBox.Show(res.Message ?? "Fiş basılamadı.", "Hata",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    PrintBtn.IsEnabled = true;
                    return;
                }

                // Sonuc kutusu
                ResultBox.Visibility = Visibility.Visible;
                if (res.IsFree)
                {
                    ResultTitle.Text = "✓ Yıkama Fişi Basıldı — ÜCRETSİZ";
                    ResultBox.Background = (Brush)new BrushConverter().ConvertFrom("#F0FFF4")!;
                    ResultTitle.Foreground = (Brush)new BrushConverter().ConvertFrom("#059669")!;
                }
                else
                {
                    ResultTitle.Text = $"✓ Yıkama Fişi Basıldı — {res.ChargedAmount:0.##} TL";
                    ResultBox.Background = (Brush)new BrushConverter().ConvertFrom("#FFF7ED")!;
                    ResultTitle.Foreground = (Brush)new BrushConverter().ConvertFrom("#C2410C")!;
                }
                ResultDetail.Text =
                    $"Plaka: {res.Plate}\n" +
                    $"Giriş: {res.EntryTime:dd.MM.yyyy HH:mm}\n" +
                    $"İçeride: {res.MinutesIn} dk  ·  Ücretsiz süre: {res.FreeMinutes} dk\n" +
                    $"Fiş Saati: {res.ReceiptTime:dd.MM.yyyy HH:mm}\n" +
                    $"Tutar: {res.ChargedAmount:0.##} TL";

                // Yazdir (basit metin fis)
                PrintReceiptDocument(res);

                // Listeyi yenile (artik alreadyWashed=true gelir)
                await LoadAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Fiş basma hatası: " + ex.Message, "Hata",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                PrintBtn.IsEnabled = true;
            }
        }

        /// <summary>Basit yikama fisi yazdirma (varsayilan yazici).</summary>
        private static void PrintReceiptDocument(WashReceiptResult res)
        {
            try
            {
                var doc = new System.Windows.Documents.FlowDocument
                {
                    PagePadding = new Thickness(20),
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 14,
                    ColumnWidth = 300
                };
                void Line(string t, bool bold = false, double size = 14)
                {
                    var p = new System.Windows.Documents.Paragraph(new System.Windows.Documents.Run(t))
                    { Margin = new Thickness(0), FontSize = size };
                    if (bold) p.FontWeight = FontWeights.Bold;
                    doc.Blocks.Add(p);
                }
                Line("YIKAMA FİŞİ", true, 18);
                Line("Kapalı Otopark");
                Line("--------------------------------");
                Line($"Plaka      : {res.Plate}", true);
                Line($"Giriş      : {res.EntryTime:dd.MM.yyyy HH:mm}");
                Line($"İçeride    : {res.MinutesIn} dk");
                Line($"Ücretsiz   : {res.FreeMinutes} dk");
                Line($"Fiş Saati  : {res.ReceiptTime:dd.MM.yyyy HH:mm}");
                Line("--------------------------------");
                Line($"TUTAR      : {res.ChargedAmount:0.##} TL", true, 16);
                Line("");
                Line(res.IsFree ? "(Ücretsiz süre içinde)" : "(Ücretsiz süre aşıldı)");

                var pd = new System.Windows.Controls.PrintDialog();
                // Diyalog gostermeden varsayilan yazici (kiosk icin). Diyalog isteniyorsa pd.ShowDialog().
                System.Windows.Documents.IDocumentPaginatorSource src = doc;
                pd.PrintDocument(src.DocumentPaginator, "Yikama Fisi - " + res.Plate);
            }
            catch
            {
                // Yazici yoksa sessiz gec — fis zaten DB'ye kaydedildi.
            }
        }
    }

    public sealed class WashRow
    {
        public long EntryId { get; set; }
        public string Plate { get; set; } = "";
        public int MinutesIn { get; set; }
        public DateTime EntryTime { get; set; }
        public bool AlreadyWashed { get; set; }

        /// <summary>
        /// Ucretsiz sureden kalan dakika. SUNUCUNUN hesabidir (WashController),
        /// istemci saatiyle yeniden hesaplanmaz; otopark bilgisayarinin saati
        /// kaymissa kartta celiskili iki sayi cikmasin diye boyle secildi.
        /// </summary>
        public int RemainingMinutes { get; set; }

        /// <summary>
        /// Fisi basilmis araclarda kalan sure. Deger, listenin yenilendigi ana aittir
        /// (bu ekranda geri sayim yok; "Yenile" ile tazelenir).
        /// </summary>
        public string KalanText => !AlreadyWashed
            ? ""
            : (RemainingMinutes > 0 ? $"Kalan: {RemainingMinutes} dk" : "Ücretsiz süre doldu");

        /// <summary>Suresi dolan KIRMIZI. Yesil bu sistemde "ucretsiz/devam ediyor" demek.</summary>
        public Brush KalanBrush => RemainingMinutes > 0
            ? (Brush)new BrushConverter().ConvertFrom("#059669")!
            : (Brush)new BrushConverter().ConvertFrom("#DC2626")!;

        public string WashedText => AlreadyWashed ? "FİŞLİ" : "BEKLİYOR";
        public Brush WashedBrush => AlreadyWashed
            ? (Brush)new BrushConverter().ConvertFrom("#9CA3AF")!
            : (Brush)new BrushConverter().ConvertFrom("#5ACF90")!;
    }
}
