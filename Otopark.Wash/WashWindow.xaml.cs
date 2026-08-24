using Otopark.Api.Services;
using Otopark.Core.Session;
using System;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Otopark.Wash
{
    public partial class WashWindow : Window
    {
        private readonly VehicleParkApiService _api;
        public ObservableCollection<WashRow> Rows { get; } = new();

        /// <summary>
        /// YIKANACAKLAR listesi: fisi basilmis (WASH_RECEIPT'e kaydedilmis) araclar.
        /// Ayri panelde gosterilir; geri sayim burada da devam eder.
        /// </summary>
        public ObservableCollection<WashRow> WashRows { get; } = new();

        private WashRow? _selected;

        /// <summary>Geri sayimi her saniye tazeleyen zamanlayici.</summary>
        private System.Windows.Threading.DispatcherTimer? _tick;
        /// <summary>Son sunucu yenilemesi (ucret guncel kalsin diye periyodik yenilenir).</summary>
        private DateTime _lastServerRefresh = DateTime.MinValue;

        /// <summary>Listede gosterilecek zaman araligi (dk). "Son 1 saat".</summary>
        private const int SON_DAKIKA = 60;

        /// <summary>
        /// YIKANACAKLAR paneli icin pencere (dk). Sag liste "son 1 saat" olarak KALIR;
        /// fisi basilmis arac girisinin uzerinden bir saat gecse de bu panelde kalmalidir,
        /// cunku ucretsiz suresi (varsayilan 120 dk) hala devam ediyor olabilir.
        /// 22.08'de liste "son 1 saat"e cevrilince fisli araclar 61. dakikada panelden
        /// dusuyor, kartla birlikte geri sayimlari da kayboluyordu.
        /// </summary>
        private const int YIKANACAK_DAKIKA = 480;

        public WashWindow(VehicleParkApiService api)
        {
            InitializeComponent();
            _api = api;
            EntryList.ItemsSource = Rows;
            WashList.ItemsSource = WashRows;
            Loaded += async (_, __) =>
            {
                await LoadAsync();
                StartCountdownTimer();
            };
            Closed += (_, __) => _tick?.Stop();
        }

        /// <summary>
        /// Saniyede bir tum satirlarin geri sayimini tazeler. Ayrica sunucudan gelen
        /// ucret bilgisi guncel kalsin diye 60 sn'de bir liste yeniden yuklenir.
        /// </summary>
        private void StartCountdownTimer()
        {
            _tick = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _tick.Tick += async (_, __) =>
            {
                foreach (var r in Rows) r.RefreshCountdown();
                foreach (var r in WashRows) r.RefreshCountdown();   // YIKANACAKLAR listesi de geriye saymaya devam eder

                // Sure dolan arac varsa ucret sunucudan gelir; dakikada bir tazele.
                if ((DateTime.Now - _lastServerRefresh).TotalSeconds >= 60)
                    await LoadAsync(keepSelection: true);
            };
            _tick.Start();
        }

        private async System.Threading.Tasks.Task LoadAsync(bool keepSelection = false)
        {
            try
            {
                long onceSecili = keepSelection ? (_selected?.EntryId ?? 0) : 0;

                Rows.Clear();
                if (!keepSelection)
                {
                    _selected = null;
                    SelectedPlateText.Text = "—";
                    SelectedInfoText.Text = "Sağdaki listeden bir araç seçiniz.";
                    PrintBtn.IsEnabled = false;
                    ResultBox.Visibility = Visibility.Collapsed;
                }

                // LISTE ARTIK "SON 15 ARAC" DEGIL "SON 1 SAAT" (22.08.2026).
                // Sabit sayi yanilticiydi: yogun saatte bir saat icinde giren araclarin
                // bir kismi listeye hic girmiyor, sakin saatte ise saatler once girmis
                // araclar listede kaliyordu.
                //
                // Suzgec HEM sunucuda HEM burada uygulanir: sunucudaki API guncellenmemis
                // olsa da (minutes parametresini yok sayarsa) ekran dogru calisir.
                // Sunucudan GENIS pencere istenir, sag liste burada 1 saate suzulur.
                // Boylece YIKANACAKLAR paneli sag listenin penceresine bagimli kalmaz.
                var list = await _api.GetWashRecentEntriesAsync(
                    UserSession.CompanyId, take: 200, minutes: YIKANACAK_DAKIKA);
                _lastServerRefresh = DateTime.Now;

                var sinir = DateTime.Now.AddMinutes(-SON_DAKIKA);
                var genisSinir = DateTime.Now.AddMinutes(-YIKANACAK_DAKIKA);
                var tumSatirlar = new List<WashRow>();

                foreach (var e in list)
                {
                    if (e.EntryTime < genisSinir) continue;   // API eskiyse burada elenir

                    var satir = new WashRow
                    {
                        EntryId = e.EntryId,
                        Plate = e.Plate ?? "",
                        MinutesIn = e.MinutesIn,
                        EntryTime = e.EntryTime,
                        AlreadyWashed = e.AlreadyWashed,
                        FreeMinutes = e.FreeMinutes > 0 ? e.FreeMinutes : 120,
                        Fee = e.Fee
                    };
                    tumSatirlar.Add(satir);

                    // SAG LISTE: davranis aynen korunur -> son 1 saatte giren araclar.
                    if (e.EntryTime >= sinir) Rows.Add(satir);
                }

                // YIKANACAKLAR listesi: fisi basilmis araclar ayri panelde toplanir.
                // Sag listenin 1 saatlik penceresinden BAGIMSIZ beslenir.
                WashRows.Clear();
                foreach (var r in tumSatirlar.Where(r => r.AlreadyWashed).OrderBy(r => r.Remaining))
                    WashRows.Add(r);
                WashListInfoText.Text = WashRows.Count == 0
                    ? "Fişi basılan araç yok"
                    : $"{WashRows.Count} araç yıkanacak";

                // Otomatik yenilemede secili arac korunur (kullanicinin secimi kaybolmasin).
                if (onceSecili > 0)
                {
                    var yeni = Rows.FirstOrDefault(r => r.EntryId == onceSecili);
                    if (yeni != null) EntryList.SelectedItem = yeni;
                }

                if (Rows.Count == 0 && !keepSelection)
                    SelectedInfoText.Text = "Son 1 saat içinde giriş yapmış araç yok.";
            }
            catch (Exception ex)
            {
                if (!keepSelection)
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
                // SOL PANELDE KALAN DAKIKA (24.08.2026): personel fisi basinca ucretsiz
                // sureden ne kadar kaldigini ortadaki karti aramadan burada da gorsun.
                // Deger DONUKTUR (geri saymaz), bu yuzden "Fis aninda kalan" diye
                // etiketlendi; canli geri sayim YIKANACAKLAR kartinda zaten var.
                // FreeMinutes 0 gelirse (sunucu bildirmediyse) kalan satiri hic yazilmaz;
                // aksi halde baslikta "UCRETSIZ", altta "sure doldu" celiskisi olusurdu.
                int serbestDk = res.FreeMinutes;
                int kalanDk = serbestDk - res.MinutesIn;
                ResultDetail.Text =
                    $"Plaka: {res.Plate}\n" +
                    $"Giriş: {res.EntryTime:dd.MM.yyyy HH:mm}\n" +
                    $"İçeride: {res.MinutesIn} dk  ·  Ücretsiz süre: {serbestDk} dk\n" +
                    (serbestDk > 0
                        ? (kalanDk > 0 ? $"Fiş anında kalan: {kalanDk} dk\n" : "Ücretsiz süre doldu\n")
                        : "") +
                    $"Fiş Saati: {res.ReceiptTime:dd.MM.yyyy HH:mm}\n" +
                    $"Tutar: {res.ChargedAmount:0.##} TL";

                PrintReceiptDocument(res);

                await LoadAsync();

                // FIS SONUCU KUTUSU LoadAsync ICINDE GIZLENIYORDU.
                // LoadAsync (keepSelection=false) secimi sifirlarken ResultBox'i
                // Collapsed yapiyor; kutu WPF hic cizmeden kayboluyordu, yani personel
                // fis sonucunu ve kalan dakikayi HIC goremiyordu. Metinler duruyor,
                // liste yenilendikten SONRA kutu tekrar gosteriliyor.
                ResultBox.Visibility = Visibility.Visible;
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
                Line($"Fiş Saati  : {res.ReceiptTime:dd.MM.yyyy HH:mm}");
                Line("--------------------------------");
                Line($"TUTAR      : {res.ChargedAmount:0.##} TL", true, 16);
                Line("");
                Line(res.IsFree ? "(Ücretsiz süre içinde)" : "(Ücretsiz süre aşıldı)");

                var pd = new System.Windows.Controls.PrintDialog();
                System.Windows.Documents.IDocumentPaginatorSource src = doc;
                pd.PrintDocument(src.DocumentPaginator, "Yikama Fisi - " + res.Plate);
            }
            catch
            {
                // Yazici yoksa sessiz gec — fis zaten DB'ye kaydedildi.
            }
        }
    }

    public sealed class WashRow : System.ComponentModel.INotifyPropertyChanged
    {
        public long EntryId { get; set; }
        public string Plate { get; set; } = "";
        public int MinutesIn { get; set; }
        public DateTime EntryTime { get; set; }
        public bool AlreadyWashed { get; set; }

        /// <summary>Tanimli ucretsiz sure (dk) — WASH_SETTING'ten gelir.</summary>
        public int FreeMinutes { get; set; }
        /// <summary>Sunucunun bildirdigi ucret (sure dolduysa dolu).</summary>
        public decimal Fee { get; set; }

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

        /// <summary>
        /// Ucretsiz surenin bitimine kalan sure. Giris zamani + serbest sureden ANLIK hesaplanir,
        /// bu yuzden ekranda gercek zamanli olarak geriye sayar (159, 158, ...).
        /// </summary>
        public TimeSpan Remaining
        {
            get
            {
                var bitis = EntryTime.AddMinutes(FreeMinutes);
                var kalan = bitis - DateTime.Now;
                return kalan > TimeSpan.Zero ? kalan : TimeSpan.Zero;
            }
        }

        public bool IsExpired => Remaining <= TimeSpan.Zero;

        /// <summary>
        /// Aracin otoparkta gecirdigi sure — giris zamanindan ANLIK hesaplanir.
        /// Sunucudan gelen MinutesIn tek seferlik bir fotograftir; bu ise geri sayimla
        /// birlikte her saniye tazelenir.
        /// </summary>
        public TimeSpan IcerideSure
        {
            get
            {
                var gecen = DateTime.Now - EntryTime;
                return gecen > TimeSpan.Zero ? gecen : TimeSpan.Zero;
            }
        }

        /// <summary>"2 sa 15 dk iceride" / "43 dk iceride"</summary>
        public string IcerideText
        {
            get
            {
                var s = IcerideSure;
                return s.TotalHours >= 1
                    ? $"{(int)s.TotalHours} sa {s.Minutes} dk içeride"
                    : $"{s.Minutes} dk içeride";
            }
        }

        /// <summary>Listede gosterilen sure/ucret metni.</summary>
        public string CountdownText => IsExpired
            ? (Fee > 0 ? $"Süre doldu — Ücret: {Fee:0.##} ₺" : "Süre doldu — ücretli")
            : $"Kalan: {(int)Remaining.TotalMinutes} dk {Remaining.Seconds:D2} sn";

        public Brush CountdownBrush => IsExpired
            ? (Brush)new BrushConverter().ConvertFrom("#DC2626")!   // kirmizi: sure doldu
            : (Brush)new BrushConverter().ConvertFrom("#059669")!;  // yesil: devam ediyor

        /// <summary>Zamanlayici her saniye cagirir: geri sayim alanlarini tazeler.</summary>
        public void RefreshCountdown()
        {
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Remaining)));
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsExpired)));
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(CountdownText)));
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(CountdownBrush)));
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IcerideSure)));
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IcerideText)));
        }

        // YIKANACAK: fis basilmis, arac cikista yikama ile karsilanacak (WASH_RECEIPT'e kalici
        // kaydedildi - raporlarda kullanilir). Geri sayim bu isaretlemeden sonra da DEVAM eder.
        public string WashedText => AlreadyWashed ? "YIKANACAK" : "BEKLİYOR";
        public Brush WashedBrush => AlreadyWashed
            ? (Brush)new BrushConverter().ConvertFrom("#2563EB")!   // mavi: yikanacak (dikkat cekici)
            : (Brush)new BrushConverter().ConvertFrom("#5ACF90")!;
    }
}
