using System;
using System.Threading;
using System.Threading.Tasks;

namespace Otopark.Core.Offline;

public enum OfflineMod { Cevrimici, Cevrimdisi, Senkron }

/// <summary>
/// BAĞLANTI DURUMU / DEVRE KESİCİ (20.09.2026). Bkz. PLAN_OFFLINE_CALISMA.md Bölüm 5.
///
/// ÇEVRİMİÇİ: normal akış (sunucuya doğrudan gidilir).
/// ÇEVRİMDIŞI: 3 ardışık nabız hatasından sonra; iş çağrıları HİÇ denenmeden yerel kuyruğa
///             yazılır (K1 - bariyer/fiş öncesi kalıcı yazım gecikmesin diye sunucu HİÇ beklenmez).
/// SENKRON: nabız 2 kez arka arkaya başarılı olunca; kuyruk boşalana kadar bu moddadır,
///          yeni işlemler de kuyruktan geçmeye devam eder (sıra bozulmasın).
///
/// UZAKTAN KAPATMA (K10): sunucu OfflineIzinli=false derse mod HİÇBİR ZAMAN Cevrimdisi'ye geçmez
/// (sunucu erişilemese bile senkron akışı yerine "sunucuya ulaşılamıyor" hatası eski davranışla verilir).
/// </summary>
public sealed class BaglantiDurumu
{
    private readonly SahaOfflineClient _client;
    private readonly Func<SahaNabizIstek> _nabizBilgisiUret;
    private readonly Action<string, string>? _olayLogu;

    private int _ardisikHata;
    private int _ardisikBasari;
    private volatile bool _offlineIzinli = true;
    private volatile bool _calisiyor;
    private CancellationTokenSource? _cts;

    public OfflineMod Mod { get; private set; } = OfflineMod.Cevrimici;
    public bool OfflineIzinli => _offlineIzinli;
    public bool BekleyenKomutVar { get; private set; }

    public event Action<OfflineMod, OfflineMod>? ModDegisti;

    public BaglantiDurumu(SahaOfflineClient client, Func<SahaNabizIstek> nabizBilgisiUret, Action<string, string>? olayLogu = null)
    {
        _client = client;
        _nabizBilgisiUret = nabizBilgisiUret;
        _olayLogu = olayLogu;
    }

    public void Baslat(TimeSpan cevrimdisiAralik, TimeSpan cevrimiciAralik)
    {
        if (_calisiyor) return;
        _calisiyor = true;
        _cts = new CancellationTokenSource();
        _ = DongueAsync(cevrimdisiAralik, cevrimiciAralik, _cts.Token);
    }

    public void Durdur() => _cts?.Cancel();

    private async Task DongueAsync(TimeSpan cevrimdisiAralik, TimeSpan cevrimiciAralik, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var basarili = await _client.ErisilebilirMiAsync(_nabizBilgisiUret(), ct);
                if (_client.SonOfflineIzinli is bool izin) _offlineIzinli = izin;
                if (basarili)
                {
                    _ardisikHata = 0;
                    _ardisikBasari++;
                    if (Mod == OfflineMod.Cevrimdisi && _ardisikBasari >= 2)
                        GecisYap(OfflineMod.Senkron, "Sunucuya yeniden ulaşıldı; senkron başlıyor.");
                }
                else
                {
                    _ardisikBasari = 0;
                    _ardisikHata++;
                    if (Mod == OfflineMod.Cevrimici && _ardisikHata >= 3 && _offlineIzinli)
                        GecisYap(OfflineMod.Cevrimdisi, "Sunucuya 3 denemede ulaşılamadı; çevrimdışı moda geçildi.");
                    else if (Mod == OfflineMod.Cevrimici && _ardisikHata >= 3 && !_offlineIzinli)
                        _olayLogu?.Invoke("UYARI", "Sunucuya ulaşılamıyor ama offlineIzinli=false; çevrimdışı moda GEÇİLMEDİ (K10).");
                }
            }
            catch { /* nabız döngüsü asla çökmemeli */ }

            var bekleme = Mod == OfflineMod.Cevrimici ? cevrimiciAralik : cevrimdisiAralik;
            try { await Task.Delay(bekleme, ct); } catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>
    /// İŞ ÇAĞRISI AĞ HATASI (09.10.2026). Saha: "offline bariyer açma çalışmıyor."
    ///
    /// Nabız çevrimiçiyken 60 sn'de bir atılıyor ve 3 ardışık hata isteniyordu: bağlantı
    /// koptuğunda çevrimdışına geçiş 2-3,5 dk sürüyor, bu sürede her giriş/çıkış sunucuyu
    /// bekleyip "API hatası / borç bilgisi alınamadı" ile duruyor, bariyer açılmıyordu.
    /// Kısa kesintilerde çevrimdışı mod HİÇ devreye girmiyordu.
    ///
    /// Artık bir iş çağrısı ağ hatası alınca çağıran bunu bildirir: HEMEN tek bir nabız
    /// denenir; o da ulaşamazsa beklemeden çevrimdışına geçilir ve çağıran işlemi yerel
    /// akışla tamamlar. Sunucu cevap veriyorsa (hata başka nedenliyse) mod DEĞİŞMEZ.
    /// OfflineIzinli=false ise (K10) hiç geçilmez. Döner: şu an çevrimdışı/senkron mu.
    /// </summary>
    public async Task<bool> AgHatasiSonrasiKontrolAsync(string kaynak)
    {
        if (Mod != OfflineMod.Cevrimici) return true;
        if (!_offlineIzinli) return false;

        bool erisilebilir;
        try { erisilebilir = await _client.ErisilebilirMiAsync(_nabizBilgisiUret()); }
        catch { erisilebilir = false; }
        if (erisilebilir) return false;

        if (Mod == OfflineMod.Cevrimici)
            GecisYap(OfflineMod.Cevrimdisi, $"İş çağrısı ağ hatası ({kaynak}) + anlık nabız başarısız; çevrimdışı moda geçildi.");
        return true;
    }

    /// <summary>İstisna (ya da iç istisnası) bir AĞ hatası mı: zaman aşımı, bağlantı reddi, DNS.</summary>
    public static bool AgHatasiMi(Exception? ex)
    {
        for (var e = ex; e != null; e = e.InnerException)
            if (e is System.Net.Http.HttpRequestException || e is TaskCanceledException ||
                e is OperationCanceledException || e is System.Net.Sockets.SocketException ||
                e is TimeoutException)
                return true;
        return false;
    }

    /// <summary>Senkron tamamlanınca (kuyruk boş) çağrılır; Çevrimiçi'ye döner.</summary>
    public void SenkronTamamlandi()
    {
        if (Mod == OfflineMod.Senkron) GecisYap(OfflineMod.Cevrimici, "Kuyruk boşaldı; çevrimiçi.");
    }

    /// <summary>Sunucu OfflineIzinli bilgisini günceller (K10 - uzaktan kapatma).</summary>
    public void OfflineIzinliGuncelle(bool izinli) => _offlineIzinli = izinli;

    private void GecisYap(OfflineMod yeni, string mesaj)
    {
        var eski = Mod;
        Mod = yeni;
        _ardisikHata = 0;
        _ardisikBasari = 0;
        _olayLogu?.Invoke("MOD_DEGISTI", $"{eski} -> {yeni}: {mesaj}");
        ModDegisti?.Invoke(eski, yeni);
    }
}
