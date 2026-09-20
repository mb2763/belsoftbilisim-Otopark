using System;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Otopark.Core.Offline;

/// <summary>
/// KİOSK↔MASAÜSTÜ LAN KÖPRÜSÜ — masaüstü tarafı (21.09.2026). Bkz. PLAN_OFFLINE_CALISMA.md
/// Bölüm 6.4.4, 8. Sunucuya (ikisi de) ulaşılamadığında kiosk'un yerel kuyruğa yazdığı bir
/// ödemeyi masaüstüne DOĞRUDAN (LAN üzerinden, sunucudan geçmeden) bildirir; masaüstü bunu
/// yalnızca BİLGİLENDİRME/olay kaydı olarak kullanır (bariyer kararını hâlâ kendi kuralları
/// verir) — LAN bildirimi hiçbir zaman tek başına "borç kapandı" sayılmaz, yalnızca personelin
/// ekranda görmesi ve gerekirse "Kiosk Ödeme Kodu" ile hızlı doğrulaması içindir.
///
/// GÜVENLİK SINIRI: bu HMAC, cihaz kimliğine değil PAYLAŞILAN SABİT bir siteanahtarına
/// (appsettings "Offline:SahaAjaniAnahtari", kiosk ve masaüstünde AYNI değer) dayanır - tıpkı
/// SahaCihazManager'ın kendi belgesindeki gibi, güvenlik sınırı LAN/VPN'in kendisidir, HMAC
/// yalnızca rastgele bir cihazın yanlışlıkla/kötü niyetle istek atmasını zorlaştırır.
///
/// KURULUM NOTU: "http://+:port/" ön eki Windows'ta ya Yönetici olarak çalışmayı ya da önceden
/// bir URL ACL rezervasyonu ister (`netsh http add urlacl url=http://+:5088/ user=Everyone`).
/// Rezervasyon yoksa Baslat() başarısız olur ve olay günlüğüne yazar; masaüstü ÇALIŞMAYA DEVAM
/// EDER (LAN köprüsü olmadan) - bu özellik olmadan da K1/K7 fallback'i (doğrulama kodu elle
/// girme) çalışır, yalnızca "otomatik anında bildirim" kısmı devre dışı kalır.
/// </summary>
public sealed class SahaAjaniSunucusu
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private readonly int _port;
    private readonly string _anahtar;
    private readonly Action<string, string> _olayLogu;
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;

    /// <summary>Bir kiosk ödeme bildirimi doğrulanıp kabul edildiğinde tetiklenir.</summary>
    public event Action<OdemeBildirimIstek>? OdemeBildirimiAlindi;

    public bool Calisiyor => _listener != null;

    public SahaAjaniSunucusu(int port, string anahtar, Action<string, string>? olayLogu = null)
    {
        _port = port;
        _anahtar = anahtar ?? "";
        _olayLogu = olayLogu ?? ((_, __) => { });
    }

    public void Baslat()
    {
        if (_listener != null) return;
        if (string.IsNullOrWhiteSpace(_anahtar))
        {
            _olayLogu("SAHA_AJANI_KAPALI", "Offline:SahaAjaniAnahtari boş - LAN köprüsü başlatılmadı.");
            return;
        }

        try
        {
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://+:{_port}/");
            listener.Start();
            _listener = listener;
        }
        catch (Exception ex)
        {
            _olayLogu("SAHA_AJANI_HATA",
                $"Başlatılamadı (port={_port}): {ex.Message}. " +
                "Yönetici olarak çalıştırın ya da 'netsh http add urlacl url=http://+:" + _port + "/ user=Everyone' komutunu bir kez çalıştırın.");
            return;
        }

        _cts = new CancellationTokenSource();
        _ = DinleAsync(_cts.Token);
        _olayLogu("SAHA_AJANI", $"Başlatıldı, port={_port}");
    }

    public void Durdur()
    {
        _cts?.Cancel();
        try { _listener?.Stop(); } catch { }
        try { _listener?.Close(); } catch { }
        _listener = null;
    }

    private async Task DinleAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener != null)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch { break; }
            _ = Task.Run(() => IstegiIsleAsync(ctx));
        }
    }

    private async Task IstegiIsleAsync(HttpListenerContext ctx)
    {
        try
        {
            if (ctx.Request.HttpMethod != "POST" || ctx.Request.Url?.AbsolutePath != "/odeme")
            {
                ctx.Response.StatusCode = 404;
                ctx.Response.Close();
                return;
            }

            string body;
            using (var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding))
                body = await reader.ReadToEndAsync();

            var zaman = ctx.Request.Headers["X-Saha-Zaman"] ?? "";
            var imza = ctx.Request.Headers["X-Saha-Imza"] ?? "";
            var istek = JsonSerializer.Deserialize<OdemeBildirimIstek>(body, JsonOpts);

            if (istek == null || !LanImza.Dogrula(_anahtar, istek.CihazId ?? "", zaman, imza))
            {
                ctx.Response.StatusCode = 401;
                ctx.Response.Close();
                _olayLogu("SAHA_AJANI_RED", "Doğrulanamayan LAN isteği reddedildi.");
                return;
            }

            _olayLogu("KIOSK_ODEME_LAN",
                $"plaka={istek.Plaka} tutar={istek.Tutar} kod={istek.DogrulamaKodu} islemNo={istek.IslemNo}");

            try { OdemeBildirimiAlindi?.Invoke(istek); }
            catch { /* dinleyici hatası isteği bozmasın */ }

            var yanitBytes = JsonSerializer.SerializeToUtf8Bytes(new OdemeBildirimYanit { Alindi = true }, JsonOpts);
            ctx.Response.ContentType = "application/json";
            ctx.Response.StatusCode = 200;
            await ctx.Response.OutputStream.WriteAsync(yanitBytes, 0, yanitBytes.Length);
            ctx.Response.Close();
        }
        catch (Exception ex)
        {
            try { ctx.Response.StatusCode = 500; ctx.Response.Close(); } catch { }
            _olayLogu("SAHA_AJANI_HATA", ex.Message);
        }
    }
}

public sealed class OdemeBildirimIstek
{
    public string CihazId { get; set; } = "";
    public string Plaka { get; set; } = "";
    public decimal Tutar { get; set; }
    public string DogrulamaKodu { get; set; } = "";
    public string IslemNo { get; set; } = "";
    public DateTime Zaman { get; set; }
}

public sealed class OdemeBildirimYanit
{
    public bool Alindi { get; set; }
    public string? Mesaj { get; set; }
}

/// <summary>
/// Paylaşılan-sabit-anahtarlı LAN HMAC şeması (kiosk ve masaüstünde AYNI kod kopyalanır).
/// CihazKimligi.Imzala/SahaCihazManager.DogrulaImza ile AYNI matematik (SHA-256'lanmış
/// anahtar + HMAC-SHA256), ama anahtar sunucu tarafından verilen kişiye özel bir sır değil,
/// appsettings'e elle yazılan PAYLAŞILAN bir site anahtarıdır.
/// </summary>
public static class LanImza
{
    public static string Uret(string anahtar, string cihazId, string zamanIsoUtc)
    {
        using var sha = SHA256.Create();
        var anahtarBaytlari = sha.ComputeHash(Encoding.UTF8.GetBytes(anahtar ?? ""));
        using var hmac = new HMACSHA256(anahtarBaytlari);
        var imza = hmac.ComputeHash(Encoding.UTF8.GetBytes($"{cihazId}|{zamanIsoUtc}"));
        return Convert.ToHexString(imza);
    }

    public static bool Dogrula(string anahtar, string cihazId, string zamanIsoUtc, string gelenImza)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(anahtar) || string.IsNullOrWhiteSpace(gelenImza)) return false;
            if (!DateTime.TryParse(zamanIsoUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out var zaman))
                return false;
            if (Math.Abs((DateTime.UtcNow - zaman.ToUniversalTime()).TotalMinutes) > 5) return false;

            var beklenen = Uret(anahtar, cihazId, zamanIsoUtc);
            return string.Equals(beklenen, gelenImza, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
}
