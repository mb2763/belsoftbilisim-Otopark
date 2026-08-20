using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Otopark.Core.Services;

namespace Otopark.Client.Services;

public record BarrierResult(bool Success, string Message);

/// <summary>Bariyer komutunun nasil gonderilecegi (marka bazinda degisir).</summary>
internal sealed record BarrierCommand(HttpMethod Method, string Url, string? XmlBody, bool NeedsPulse);

/// <summary>
/// Bariyer, KAMERANIN alarm cikis (IO/role) portundan tetiklenir; yani bariyer IP'si = kamera IP'si.
///
/// DESTEKLENEN MARKALAR
///   - HIKVISION (yeni kameralar, orn. iDS-2CD7A46G0/P-IZHS) — ISAPI:
///         PUT http://IP/ISAPI/System/IO/outputs/{port}/trigger
///         Govde: &lt;IOPortData&gt;&lt;outputState&gt;high|low&lt;/outputState&gt;&lt;/IOPortData&gt;
///     Role DARBE (pulse) seklinde tetiklenir: once "high", PulseMs sonra "low".
///     Kimlik dogrulama genelde DIGEST'tir (Basic cogu Hikvision'da kapalidir).
///
///   - AXIS (eski kameralar) — CGI:
///         GET http://IP/axis-cgi/io/port.cgi?action=2:/1000\
///     Darbe suresi URL'in kendisinde oldugu icin ayrica "low" gonderilmez.
///
/// MARKA NASIL BELIRLENIR
///   1) appsettings "Barrier:Brand" ("Hikvision" | "Axis") acikca verilmisse o kullanilir.
///   2) Verilmemisse komut URL'inden anlasilir (ISAPI -> Hikvision, axis-cgi -> Axis).
///   3) Hicbiri yoksa VARSAYILAN HIKVISION'dur (sahadaki yeni kameralar).
/// </summary>
public static class BarrierService
{
    // Kamera/IO kimligi: once Barrier:* , yoksa Camera:* kullanilir.
    // (Hikvision guclu parola zorunlu kildigi icin kamera goruntu kimligi ile
    //  IO kimligi farkli olabiliyor.)
    // NOT: "??" YETMEZ — appsettings'te alan BOS STRING olarak birakilabiliyor ve
    // bos string null degildir; o zaman kimlik bos gider, kamera 401 doner.
    private static string User => Ilk(
        AppConfig.Configuration["Barrier:Username"],
        AppConfig.Configuration["Camera:Username"],
        "admin");

    private static string Pass => Ilk(
        AppConfig.Configuration["Barrier:Password"],
        AppConfig.Configuration["Camera:Password"],
        "admin");

    /// <summary>Ilk DOLU degeri dondurur (bos/whitespace atlanir).</summary>
    private static string Ilk(params string?[] adaylar)
    {
        foreach (var a in adaylar)
            if (!string.IsNullOrWhiteSpace(a)) return a!;
        return "";
    }

    // Digest + Basic destegi: HttpClientHandler.Credentials meydan-okuma (challenge)
    // akisini kendisi yurutur; Hikvision Digest istedigi icin bu SART.
    //
    // TEMBEL (lazy) OLUSTURULUR: static alan olarak kurulursa kimlik, AppConfig
    // yuklenmeden ONCE okunabilir ve sessizce "admin/admin"e duser.
    // Ayrica appsettings degistiginde Sifirla() ile yeniden kurulabilir.
    private static HttpClient? _http;
    private static string _httpUser = "";
    private static readonly object _httpLock = new();

    private static HttpClient Http
    {
        get
        {
            lock (_httpLock)
            {
                var u = User;
                // Kimlik degistiyse istemciyi yeniden kur.
                if (_http != null && _httpUser == u) return _http;

                _http?.Dispose();
                _httpUser = u;
                _http = new HttpClient(new HttpClientHandler
                {
                    Credentials = new NetworkCredential(u, Pass),
                    PreAuthenticate = false,
                })
                {
                    Timeout = TimeSpan.FromSeconds(5)
                };
                CameraDiag.Write($"Bariyer HTTP istemcisi kuruldu — kullanici: '{u}'");
                return _http;
            }
        }
    }

    private static string BrandSetting => (AppConfig.Configuration["Barrier:Brand"] ?? "").Trim();

    /// <summary>Hikvision alarm cikis portu (iDS-2CD7A46G0/P-IZHS'te 1 adet vardir -> 1).</summary>
    private static int OutputPort =>
        int.TryParse(AppConfig.Configuration["Barrier:OutputPort"], out var p) && p > 0 ? p : 1;

    /// <summary>Role kac ms "high" kalsin (Hikvision darbe suresi). Bariyer kartinin bekledigi sure.</summary>
    private static int PulseMs =>
        int.TryParse(AppConfig.Configuration["Barrier:PulseMs"], out var m) && m > 0 ? m : 800;

    private static int DelayMs => int.TryParse(AppConfig.Configuration["Barrier:DelayMs"], out var d) ? d : 100;

    // BEKLEME (cooldown) ARTIK PLAKA BAZLI (20.08.2026).
    //
    // ONCEKI HALI KURESELDI: bir bariyer 15 sn boyunca ikinci bir acma komutu
    // kabul etmiyordu. Amac ayni aracin plakasi birkac kez okundugunda bariyerin
    // tekrar tekrar acilmasini onlemekti. Yan etkisi agir oldu: KAPALI OTOPARKTA
    // ART ARDA GELEN ARACLARDA ikinci arac odemesini yaptigi halde bariyer
    // ACILMIYORDU (komut sessizce yutuluyordu).
    //
    // Artik bekleme (bariyer + PLAKA) ikilisine gore tutulur:
    //   - AYNI plaka tekrar okunursa  -> yutulur (asil korunmak istenen durum)
    //   - FARKLI plaka gelirse        -> bariyer HEMEN acilir
    //
    // Guvenlik acisindan zayiflama yok: bariyer komutu her durumda ancak
    // giris/cikis kaydi sunucuda BASARIYLA olustuktan sonra gonderilir; cikista
    // ayrica borc kontrolu yapilir (bkz. PersonnelDashboardViewModel).
    //
    // appsettings.json "Barrier:CooldownSeconds" ile ayarlanir (default 15 sn).
    private static int CooldownSeconds => int.TryParse(AppConfig.Configuration["Barrier:CooldownSeconds"], out var c) ? c : 15;
    private static readonly Dictionary<string, DateTime> _sonAcma = new();
    private static readonly object _cooldownLock = new();

    // Komutlar SIRAYLA gonderilir. Hikvision'da acma "high" + PulseMs sonra "low"
    // seklinde iki adimdir; iki arac ayni anda gelirse adimlar ic ice girip roleyi
    // acik birakabilir. Semafor bunu engeller, farkli plakalar sadece siraya girer.
    private static readonly SemaphoreSlim _komutKilidi = new(1, 1);

    /// <param name="plate">
    /// Bekleme bu plakaya gore uygulanir. Bos birakilirsa (elle acma dugmesi gibi)
    /// tek bir ortak anahtar kullanilir; o durumda eski kuresel davranis gecerlidir.
    /// </param>
    public static async Task<BarrierResult> OpenEntryGateAsync(string? plate = null)
    {
        if (!TryEnterCooldown(isEntry: true, plate, out int remaining))
            return new BarrierResult(false, $"Giris bariyeri: ayni plaka icin bekleme ({remaining} sn kaldi) — komut yutuldu.");

        var cmd = Build(AppConfig.Configuration["Barrier:EntryCommandUrl"], CameraConfigService.EntryUrl);
        return await SendCommandAsync(cmd, "Giris bariyeri");
    }

    public static async Task<BarrierResult> OpenExitGateAsync(string? plate = null)
    {
        if (!TryEnterCooldown(isEntry: false, plate, out int remaining))
            return new BarrierResult(false, $"Cikis bariyeri: ayni plaka icin bekleme ({remaining} sn kaldi) — komut yutuldu.");

        var cmd = Build(AppConfig.Configuration["Barrier:ExitCommandUrl"], CameraConfigService.ExitUrl);
        return await SendCommandAsync(cmd, "Cikis bariyeri");
    }

    /// <summary>
    /// Gonderilecek komutu uretir. Config'te TAM komut adresi varsa o kullanilir
    /// (bicimi URL'den anlasilir); yoksa kamera URL'indeki host'tan marka kuralina
    /// gore turetilir.
    /// </summary>
    private static BarrierCommand? Build(string? configUrl, string cameraUrl)
    {
        // 1) Config'te acik komut adresi varsa: bicimini adresten anla.
        if (!string.IsNullOrWhiteSpace(configUrl))
        {
            if (configUrl!.IndexOf("ISAPI", StringComparison.OrdinalIgnoreCase) >= 0)
                return new BarrierCommand(HttpMethod.Put, configUrl, IoXml("high"), NeedsPulse: true);

            // Axis CGI ve benzeri "tek atislik" adresler: darbe URL'in icinde.
            return new BarrierCommand(HttpMethod.Get, configUrl, null, NeedsPulse: false);
        }

        // 2) Kamera URL'inden host cikar, markaya gore komut kur.
        if (string.IsNullOrWhiteSpace(cameraUrl)) return null;

        string host;
        try
        {
            host = new Uri(cameraUrl).Host;
            if (string.IsNullOrWhiteSpace(host)) return null;
        }
        catch { return null; }

        if (IsAxis(cameraUrl))
            return new BarrierCommand(HttpMethod.Get,
                $"http://{host}/axis-cgi/io/port.cgi?action=2:/1000\\", null, NeedsPulse: false);

        // VARSAYILAN: Hikvision ISAPI
        return new BarrierCommand(HttpMethod.Put,
            $"http://{host}/ISAPI/System/IO/outputs/{OutputPort}/trigger", IoXml("high"), NeedsPulse: true);
    }

    /// <summary>Marka Axis mi? Once acik ayar, sonra URL ipucu.</summary>
    private static bool IsAxis(string cameraUrl)
    {
        if (BrandSetting.Equals("Axis", StringComparison.OrdinalIgnoreCase)) return true;
        if (BrandSetting.Equals("Hikvision", StringComparison.OrdinalIgnoreCase)) return false;
        return cameraUrl.IndexOf("axis-cgi", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>Hikvision ISAPI alarm cikis govdesi.</summary>
    private static string IoXml(string state) =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
        "<IOPortData version=\"2.0\" xmlns=\"http://www.isapi.org/ver20/XMLSchema\">" +
        $"<outputState>{state}</outputState>" +
        "</IOPortData>";

    private static bool TryEnterCooldown(bool isEntry, string? plate, out int remainingSec)
    {
        int cd = Math.Max(0, CooldownSeconds);
        if (cd <= 0) { remainingSec = 0; return true; }

        // Anahtar: bariyer + normallestirilmis plaka. Plaka yoksa ortak anahtar.
        string plakaAnahtari = (plate ?? "").ToUpperInvariant().Trim().Replace(" ", "");
        if (plakaAnahtari.Length == 0) plakaAnahtari = "(plakasiz)";
        string anahtar = (isEntry ? "G|" : "C|") + plakaAnahtari;

        lock (_cooldownLock)
        {
            if (_sonAcma.TryGetValue(anahtar, out var last))
            {
                var elapsed = DateTime.UtcNow - last;
                if (elapsed.TotalSeconds < cd)
                {
                    remainingSec = (int)Math.Ceiling(cd - elapsed.TotalSeconds);
                    return false;
                }
            }

            _sonAcma[anahtar] = DateTime.UtcNow;

            // Sozluk sinirsiz buyumesin: bekleme suresinin cok uzerindeki kayitlar atilir.
            if (_sonAcma.Count > 256)
            {
                var esik = DateTime.UtcNow.AddSeconds(-Math.Max(cd * 4, 300));
                var eskiler = new List<string>();
                foreach (var kv in _sonAcma)
                    if (kv.Value < esik) eskiler.Add(kv.Key);
                foreach (var k in eskiler) _sonAcma.Remove(k);
            }

            remainingSec = 0;
            return true;
        }
    }

    private static async Task<BarrierResult> SendCommandAsync(BarrierCommand? cmd, string gateName)
    {
        if (cmd == null || string.IsNullOrWhiteSpace(cmd.Url))
            return new BarrierResult(false, $"{gateName}: URL yapilandirilmamis.");

        // Acma iki adimli (high -> low) oldugu icin komutlar sirayla gonderilir.
        await _komutKilidi.WaitAsync();
        try
        {
            if (DelayMs > 0)
                await Task.Delay(DelayMs);

            var (ok, code, wwwAuth, body) = await SendOnceAsync(cmd.Method, cmd.Url, cmd.XmlBody);

            if (!ok)
            {
                CameraDiag.Error($"{gateName} komutu basarisiz: {cmd.Method} {cmd.Url} -> HTTP {code}");

                if (code == 401)
                {
                    // 401'de tahmin yurutmemek icin: HANGI kullanici gonderildi ve kamera
                    // NE istiyor (Digest mi Basic mi) acikca yazilir.
                    CameraDiag.Error(
                        $"{gateName}: KIMLIK REDDEDILDI. Gonderilen kullanici: '{User}' " +
                        $"(kaynak: {(string.IsNullOrWhiteSpace(AppConfig.Configuration["Barrier:Username"]) ? "Camera:Username" : "Barrier:Username")}). " +
                        $"Kameranin istedigi: {(string.IsNullOrWhiteSpace(wwwAuth) ? "(WWW-Authenticate basligi yok)" : wwwAuth)}. " +
                        "Parolayi appsettings.json > Barrier:Password alanina yazin. " +
                        "NOT: Hikvision arka arkaya hatali denemede IP'yi GECICI KILITLER (illegal login lock); " +
                        "parola dogruyken de 401 gorurseniz kamerayi yeniden baslatin ya da kilit suresini bekleyin.");
                }
                else if (!string.IsNullOrWhiteSpace(body))
                {
                    CameraDiag.Error($"{gateName}: cihaz yaniti -> {body}");
                }

                return new BarrierResult(false, $"{gateName} acilamadi. HTTP {code}");
            }

            // Hikvision: role "high" birakilirsa bariyer acik kalir/kart tekrar tetiklenmez.
            // PulseMs sonra "low" gonderip roleyi serbest birak.
            if (cmd.NeedsPulse)
            {
                await Task.Delay(PulseMs);
                var (lowOk, lowCode, _, _) = await SendOnceAsync(HttpMethod.Put, cmd.Url, IoXml("low"));
                if (!lowOk)
                    CameraDiag.Error($"{gateName}: role serbest birakilamadi (low) -> HTTP {lowCode}. " +
                                     "Role acik kalmis olabilir.");
            }

            return new BarrierResult(true, $"{gateName} acildi.");
        }
        catch (TaskCanceledException)
        {
            return new BarrierResult(false, $"{gateName}: Baglanti zaman asimina ugradi.");
        }
        catch (HttpRequestException ex)
        {
            return new BarrierResult(false, $"{gateName}: Baglanti hatasi - {ex.Message}");
        }
        catch (Exception ex)
        {
            return new BarrierResult(false, $"{gateName}: {ex.Message}");
        }
        finally
        {
            _komutKilidi.Release();
        }
    }

    private static async Task<(bool ok, int statusCode, string wwwAuth, string body)> SendOnceAsync(
        HttpMethod method, string url, string? xmlBody)
    {
        using var req = new HttpRequestMessage(method, url);
        if (xmlBody != null)
            req.Content = new StringContent(xmlBody, Encoding.UTF8, "application/xml");

        using var resp = await Http.SendAsync(req);

        // Tani icin: 401'de kameranin istedigi yontem (Digest/Basic) bu baslikta gelir.
        string wwwAuth = "";
        if (resp.Headers.TryGetValues("WWW-Authenticate", out var vals))
            wwwAuth = string.Join(" | ", vals);

        string body = "";
        if (!resp.IsSuccessStatusCode)
        {
            try
            {
                body = await resp.Content.ReadAsStringAsync();
                if (body.Length > 400) body = body.Substring(0, 400) + "...";
            }
            catch { }
        }

        return (resp.IsSuccessStatusCode, (int)resp.StatusCode, wwwAuth, body);
    }
}
