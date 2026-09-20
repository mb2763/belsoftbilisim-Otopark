using Microsoft.Data.Sqlite;
using System;
using System.IO;

namespace Otopark.Core.Offline;

/// <summary>
/// ÇEVRİMDIŞI ÇALIŞMA — yerel SQLite deposu (20.09.2026). Bkz. PLAN_OFFLINE_CALISMA.md
/// Bölüm 4 (K1: WAL + synchronous=FULL, K6: bütünlük + yedek).
///
/// Dosya: masaüstünde C:\Otopark\offline\otopark_offline.db, kiosk'ta
/// %LocalAppData%\ParkomatKiosk\offline\kiosk_offline.db (dosyaYolu parametreyle verilir,
/// bu sınıf her iki uygulamada da AYNI ŞEKİLDE kullanılır).
/// </summary>
public sealed class YerelDepo
{
    public string DosyaYolu { get; }
    private readonly string _baglantiMetni;
    private readonly object _kilit = new();

    public YerelDepo(string dosyaYolu)
    {
        DosyaYolu = dosyaYolu;
        Directory.CreateDirectory(Path.GetDirectoryName(dosyaYolu)!);
        _baglantiMetni = new SqliteConnectionStringBuilder { DataSource = dosyaYolu }.ToString();
        HazirlaSema();
    }

    public SqliteConnection YeniBaglanti()
    {
        var con = new SqliteConnection(_baglantiMetni);
        con.Open();
        using (var pragma = con.CreateCommand())
        {
            // K1: bariyer açılmadan/fiş basılmadan önce yazımın kalıcı olduğundan emin ol.
            pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA foreign_keys=ON;";
            pragma.ExecuteNonQuery();
        }
        return con;
    }

    private void HazirlaSema()
    {
        lock (_kilit)
        {
            using var con = YeniBaglanti();
            using var cmd = con.CreateCommand();
            cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS islem_kuyrugu (
  islem_no TEXT PRIMARY KEY,
  sira_no INTEGER,
  tur TEXT NOT NULL,
  plaka TEXT NOT NULL,
  yerel_giris_id TEXT,
  bagli_islem_no TEXT,
  olay_zamani TEXT NOT NULL,
  olay_zamani_duzeltilmis TEXT,
  kaynak TEXT NOT NULL,
  cihaz_id TEXT NOT NULL,
  kullanici_id INTEGER,
  company_id INTEGER,
  zone_id INTEGER,
  govde_json TEXT NOT NULL,
  durum TEXT NOT NULL DEFAULT 'BEKLIYOR',
  deneme INTEGER DEFAULT 0,
  son_deneme TEXT,
  sonraki_deneme TEXT,
  son_hata TEXT,
  sunucu_yaniti_json TEXT,
  sunucu_entry_id INTEGER,
  olusturma TEXT NOT NULL,
  tamamlanma TEXT
);
CREATE INDEX IF NOT EXISTS ix_kuyruk_durum ON islem_kuyrugu(durum, sira_no);
CREATE INDEX IF NOT EXISTS ix_kuyruk_plaka ON islem_kuyrugu(plaka, sira_no);
CREATE INDEX IF NOT EXISTS ix_kuyruk_ygid ON islem_kuyrugu(yerel_giris_id);

CREATE TABLE IF NOT EXISTS giris_esleme (
  yerel_giris_id TEXT PRIMARY KEY,
  sunucu_entry_id INTEGER,
  sunucu_arac_id INTEGER,
  plaka TEXT,
  bolge_id INTEGER,
  giris_zamani TEXT,
  durum TEXT
);

CREATE TABLE IF NOT EXISTS anlik_meta (anahtar TEXT PRIMARY KEY, deger TEXT);

CREATE TABLE IF NOT EXISTS anlik_giris (
  entry_id INTEGER PRIMARY KEY, arac_id INTEGER, plaka_anahtar TEXT,
  giris_zamani TEXT, bolge_id INTEGER, arac_tipi_id INTEGER, misafir INTEGER
);
CREATE INDEX IF NOT EXISTS ix_anlik_giris_plaka ON anlik_giris(plaka_anahtar);

CREATE TABLE IF NOT EXISTS anlik_borc (
  borc_id INTEGER PRIMARY KEY, arac_id INTEGER, plaka_anahtar TEXT,
  bolge_id INTEGER, entry_id INTEGER, kalan REAL, aciklama TEXT, olusturma TEXT
);
CREATE INDEX IF NOT EXISTS ix_anlik_borc_plaka ON anlik_borc(plaka_anahtar);

CREATE TABLE IF NOT EXISTS anlik_abonelik (
  abonelik_id INTEGER PRIMARY KEY, arac_id INTEGER, plaka_anahtar TEXT,
  tarife_id INTEGER, tarife_adi TEXT, baslangic TEXT, bitis TEXT, bolgeler_json TEXT, ucretli INTEGER
);
CREATE INDEX IF NOT EXISTS ix_anlik_abone_plaka ON anlik_abonelik(plaka_anahtar);

CREATE TABLE IF NOT EXISTS anlik_arac (
  arac_id INTEGER PRIMARY KEY, plaka_anahtar TEXT, plaka TEXT,
  arac_tipi_id INTEGER, tarife_id INTEGER, musteri_firma_id INTEGER, uyari_notu TEXT
);
CREATE INDEX IF NOT EXISTS ix_anlik_arac_plaka ON anlik_arac(plaka_anahtar);

CREATE TABLE IF NOT EXISTS anlik_tarife (
  tarife_detay_id INTEGER PRIMARY KEY, bolge_id INTEGER, yil INTEGER,
  ucretsiz_dakika INTEGER, opsiyonel_sure INTEGER, arac_tipleri_json TEXT, kademeler_json TEXT
);

CREATE TABLE IF NOT EXISTS olay (
  id INTEGER PRIMARY KEY AUTOINCREMENT, zaman TEXT, tur TEXT, mesaj TEXT
);
";
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>K6: bütünlük kontrolü + günlük yedek (VACUUM INTO). Uygulama açılışında çağrılır.</summary>
    public (bool tamam, string mesaj) ButunlukKontrolEt()
    {
        try
        {
            using var con = YeniBaglanti();
            using var cmd = con.CreateCommand();
            cmd.CommandText = "PRAGMA integrity_check;";
            var sonuc = cmd.ExecuteScalar()?.ToString() ?? "";
            return (sonuc == "ok", sonuc);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public void GunlukYedekAl(string yedekKlasoru)
    {
        try
        {
            Directory.CreateDirectory(yedekKlasoru);
            var hedef = Path.Combine(yedekKlasoru, $"yedek_{DateTime.Now:yyyyMMdd_HHmm}.db");
            using var con = YeniBaglanti();
            using var cmd = con.CreateCommand();
            cmd.CommandText = "VACUUM INTO $hedef;";
            cmd.Parameters.AddWithValue("$hedef", hedef);
            cmd.ExecuteNonQuery();

            // 14 günden eski yedekleri temizle.
            foreach (var f in Directory.GetFiles(yedekKlasoru, "yedek_*.db"))
                if ((DateTime.Now - File.GetLastWriteTime(f)).TotalDays > 14)
                    try { File.Delete(f); } catch { }
        }
        catch { /* yedek alinamasa da uygulama calismaya devam eder */ }
    }

    public void OlayYaz(string tur, string mesaj)
    {
        try
        {
            using var con = YeniBaglanti();
            using var cmd = con.CreateCommand();
            cmd.CommandText = "INSERT INTO olay (zaman, tur, mesaj) VALUES ($z, $t, $m);";
            cmd.Parameters.AddWithValue("$z", DateTime.Now.ToString("O"));
            cmd.Parameters.AddWithValue("$t", tur);
            cmd.Parameters.AddWithValue("$m", mesaj ?? "");
            cmd.ExecuteNonQuery();
        }
        catch { }
    }
}
