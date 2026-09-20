using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Otopark.Core.Offline;

/// <summary>
/// SAHA CİHAZI kimliği (20.09.2026). İlk açılışta bir GUID üretilir ve sunucuya
/// /Saha/Kaydol ile bildirilir; dönen gizli anahtar DPAPI ile şifrelenip diske
/// yazılır. Bkz. PLAN_OFFLINE_CALISMA.md Bölüm 7.1, 9.
///
/// İMZA SÖZLEŞMESİ (sunucudaki SahaCihazManager.DogrulaImza ile BİREBİR aynı olmalı):
///   imzaAnahtari = SHA-256(gizliAnahtar UTF8 metni) — 32 bayt, HMAC anahtarı olarak kullanılır.
///   imza = HMAC-SHA256(imzaAnahtari, "{cihazId}|{zamanIsoUtc}") -> hex.
/// </summary>
public sealed class CihazKimligi
{
    public string CihazId { get; private set; } = "";
    public string GizliAnahtar { get; private set; } = "";
    public bool Kayitli => !string.IsNullOrEmpty(GizliAnahtar);

    private readonly string _dosyaYolu;

    public CihazKimligi(string uygulamaAdi)
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), uygulamaAdi, "offline");
        Directory.CreateDirectory(dir);
        _dosyaYolu = Path.Combine(dir, "cihaz_kimlik.bin");
        Yukle();
    }

    private sealed class Kayit
    {
        public string CihazId { get; set; } = "";
        public string GizliAnahtar { get; set; } = "";
    }

    private void Yukle()
    {
        try
        {
            if (!File.Exists(_dosyaYolu))
            {
                CihazId = Guid.NewGuid().ToString("N");
                return;
            }
            var sifreli = File.ReadAllBytes(_dosyaYolu);
            var duz = ProtectedData.Unprotect(sifreli, null, DataProtectionScope.LocalMachine);
            var kayit = JsonSerializer.Deserialize<Kayit>(Encoding.UTF8.GetString(duz));
            if (kayit != null)
            {
                CihazId = kayit.CihazId;
                GizliAnahtar = kayit.GizliAnahtar;
            }
        }
        catch
        {
            CihazId = Guid.NewGuid().ToString("N");
        }
    }

    /// <summary>/Saha/Kaydol başarılı olunca çağrılır; anahtarı DPAPI ile şifreleyip kalıcı yazar.</summary>
    public void KaydiTamamla(string gizliAnahtar)
    {
        if (string.IsNullOrWhiteSpace(gizliAnahtar)) return;
        GizliAnahtar = gizliAnahtar;
        try
        {
            var json = JsonSerializer.Serialize(new Kayit { CihazId = CihazId, GizliAnahtar = GizliAnahtar });
            var sifreli = ProtectedData.Protect(Encoding.UTF8.GetBytes(json), null, DataProtectionScope.LocalMachine);
            File.WriteAllBytes(_dosyaYolu, sifreli);
        }
        catch { /* yazilamazsa bir sonraki acilista yeniden kayit denenir */ }
    }

    /// <summary>Belirtilen zaman damgasıyla imza üretir. zamanIsoUtc de birlikte istekle gönderilmelidir.</summary>
    public string Imzala(string zamanIsoUtc)
    {
        if (!Kayitli) return "";
        using var sha = SHA256.Create();
        var anahtarBaytlari = sha.ComputeHash(Encoding.UTF8.GetBytes(GizliAnahtar));
        using var hmac = new HMACSHA256(anahtarBaytlari);
        var imza = hmac.ComputeHash(Encoding.UTF8.GetBytes($"{CihazId}|{zamanIsoUtc}"));
        return Convert.ToHexString(imza);
    }
}
