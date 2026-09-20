using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Otopark.Core.Offline;

/// <summary>
/// BÖLGE ANLIK GÖRÜNTÜSÜ — sunucudan periyodik çekilir, SQLite'ta önbelleklenir.
/// Çevrimdışı kararların (abone mi, borcu var mı, ücret ne) tamamı BURADAN okunur.
/// Bkz. PLAN_OFFLINE_CALISMA.md Bölüm 4, 6.5, 10.
/// </summary>
public sealed class AnlikGoruntu
{
    private readonly YerelDepo _depo;
    private readonly SahaOfflineClient _client;
    private readonly string _cihazId;
    private long _companyId;
    private long _zoneId;
    private string? _bilinenSurum;

    public DateTime? SonBasariliAlim { get; private set; }

    public AnlikGoruntu(YerelDepo depo, SahaOfflineClient client, string cihazId)
    {
        _depo = depo;
        _client = client;
        _cihazId = cihazId;
    }

    public void BolgeAyarla(long companyId, long zoneId)
    {
        _companyId = companyId;
        _zoneId = zoneId;
    }

    public static string PlakaAnahtari(string? plaka) => (plaka ?? "").ToUpperInvariant().Replace(" ", "").Trim();

    /// <summary>Sunucudan tazeler (çevrimiçiyken periyodik çağrılır). Başarısız olursa sessizce eski önbellek kalır.</summary>
    public async Task<bool> TazeleAsync(CancellationToken ct = default)
    {
        if (_companyId <= 0 || _zoneId <= 0) return false;
        try
        {
            var (dto, basarili) = await _client.AnlikAsync(new OfflineAnlikIstek
            {
                CihazId = _cihazId,
                CompanyId = _companyId,
                ZoneId = _zoneId,
                BilinenSurum = _bilinenSurum
            }, ct);

            if (!basarili) return false;
            if (dto == null) { SonBasariliAlim = DateTime.Now; return true; } // 204: değişmemiş

            Kaydet(dto);
            _bilinenSurum = dto.Surum;
            SonBasariliAlim = DateTime.Now;
            return true;
        }
        catch { return false; }
    }

    private void Kaydet(OfflineAnlikDto dto)
    {
        using var con = _depo.YeniBaglanti();
        using var tx = con.BeginTransaction();

        void Exec(string sql, Action<SqliteCommand>? parametreler = null)
        {
            using var cmd = con.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = sql;
            parametreler?.Invoke(cmd);
            cmd.ExecuteNonQuery();
        }

        Exec("DELETE FROM anlik_giris; DELETE FROM anlik_borc; DELETE FROM anlik_abonelik; DELETE FROM anlik_arac; DELETE FROM anlik_tarife;");

        foreach (var g in dto.AcikGirisler)
            Exec("INSERT INTO anlik_giris VALUES ($id,$aid,$pa,$gz,$bid,$atid,$mis)", c =>
            {
                c.Parameters.AddWithValue("$id", g.EntryId);
                c.Parameters.AddWithValue("$aid", g.VehicleDefinitionId);
                c.Parameters.AddWithValue("$pa", g.PlakaAnahtar);
                c.Parameters.AddWithValue("$gz", g.GirisZamani.ToString("O"));
                c.Parameters.AddWithValue("$bid", g.BolgeId);
                c.Parameters.AddWithValue("$atid", g.AracTipiId);
                c.Parameters.AddWithValue("$mis", g.Misafir ? 1 : 0);
            });

        foreach (var b in dto.AcikBorclar)
            Exec("INSERT INTO anlik_borc VALUES ($id,$aid,$pa,$bid,$eid,$kalan,$ack,$olz)", c =>
            {
                c.Parameters.AddWithValue("$id", b.BorcId);
                c.Parameters.AddWithValue("$aid", b.VehicleDefinitionId);
                c.Parameters.AddWithValue("$pa", b.PlakaAnahtar);
                c.Parameters.AddWithValue("$bid", (object?)b.BolgeId ?? DBNull.Value);
                c.Parameters.AddWithValue("$eid", (object?)b.EntryId ?? DBNull.Value);
                c.Parameters.AddWithValue("$kalan", b.Kalan);
                c.Parameters.AddWithValue("$ack", b.Aciklama ?? "");
                c.Parameters.AddWithValue("$olz", b.Olusturma.ToString("O"));
            });

        foreach (var s in dto.AktifAbonelikler)
            Exec("INSERT INTO anlik_abonelik VALUES ($id,$aid,$pa,$tid,$tad,$bas,$bit,$bol,$uc)", c =>
            {
                c.Parameters.AddWithValue("$id", s.AbonelikId);
                c.Parameters.AddWithValue("$aid", s.VehicleDefinitionId);
                c.Parameters.AddWithValue("$pa", s.PlakaAnahtar);
                c.Parameters.AddWithValue("$tid", s.TarifeId);
                c.Parameters.AddWithValue("$tad", s.TarifeAdi ?? "");
                c.Parameters.AddWithValue("$bas", s.Baslangic.ToString("O"));
                c.Parameters.AddWithValue("$bit", s.Bitis?.ToString("O") ?? (object)DBNull.Value);
                c.Parameters.AddWithValue("$bol", JsonSerializer.Serialize(s.Bolgeler));
                c.Parameters.AddWithValue("$uc", s.Ucretli ? 1 : 0);
            });

        foreach (var a in dto.Araclar)
            Exec("INSERT INTO anlik_arac VALUES ($id,$pa,$pl,$atid,$tid,$mfid,$uy)", c =>
            {
                c.Parameters.AddWithValue("$id", a.AracId);
                c.Parameters.AddWithValue("$pa", a.PlakaAnahtar);
                c.Parameters.AddWithValue("$pl", a.Plaka ?? "");
                c.Parameters.AddWithValue("$atid", a.AracTipiId);
                c.Parameters.AddWithValue("$tid", a.TarifeId);
                c.Parameters.AddWithValue("$mfid", (object?)a.MusteriFirmaId ?? DBNull.Value);
                c.Parameters.AddWithValue("$uy", a.UyariNotu ?? "");
            });

        foreach (var t in dto.Tarifeler)
            Exec("INSERT INTO anlik_tarife VALUES ($id,$bid,$yil,$ud,$os,$at,$kd)", c =>
            {
                c.Parameters.AddWithValue("$id", t.TarifeDetayId);
                c.Parameters.AddWithValue("$bid", t.BolgeId);
                c.Parameters.AddWithValue("$yil", t.Yil);
                c.Parameters.AddWithValue("$ud", t.UcretsizDakika);
                c.Parameters.AddWithValue("$os", t.OpsiyonelSure);
                c.Parameters.AddWithValue("$at", JsonSerializer.Serialize(t.AracTipleri));
                c.Parameters.AddWithValue("$kd", JsonSerializer.Serialize(t.Kademeler));
            });

        void MetaYaz(string anahtar, string deger) => Exec("INSERT INTO anlik_meta VALUES ($a,$d) ON CONFLICT(anahtar) DO UPDATE SET deger=$d", c =>
        {
            c.Parameters.AddWithValue("$a", anahtar);
            c.Parameters.AddWithValue("$d", deger);
        });

        MetaYaz("surum", dto.Surum);
        MetaYaz("alinma_zamani", dto.AlinmaZamani.ToString("O"));
        MetaYaz("kapasite", dto.Kapasite.ToString());
        MetaYaz("oto_arac_tipi", dto.OtoAracTipiId.ToString());
        MetaYaz("oto_tarife", dto.OtoTarifeId.ToString());
        MetaYaz("engelli_tipler", JsonSerializer.Serialize(dto.EngelliAracTipleri));
        MetaYaz("kameralar", JsonSerializer.Serialize(dto.Kameralar));
        MetaYaz("abonelik_tipleri", JsonSerializer.Serialize(dto.AbonelikTipleri));
        MetaYaz("offline_izinli", dto.OfflineIzinli ? "1" : "0");

        tx.Commit();
    }

    // ===================== SORGU YARDIMCILARI (çevrimdışı karar için) =====================

    public sealed class AcikGirisSonuc { public long EntryId; public long VehicleDefinitionId; public DateTime GirisZamani; public long AracTipiId; public bool Misafir; }

    public AcikGirisSonuc? AcikGirisBul(string plaka)
    {
        var pa = PlakaAnahtari(plaka);
        using var con = _depo.YeniBaglanti();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT entry_id, arac_id, giris_zamani, arac_tipi_id, misafir FROM anlik_giris WHERE plaka_anahtar=$pa LIMIT 1";
        cmd.Parameters.AddWithValue("$pa", pa);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new AcikGirisSonuc
        {
            EntryId = r.GetInt64(0),
            VehicleDefinitionId = r.GetInt64(1),
            GirisZamani = DateTime.Parse(r.GetString(2)),
            AracTipiId = r.GetInt64(3),
            Misafir = r.GetInt32(4) == 1
        };
    }

    public decimal AcikBorcToplam(string plaka, long? bolgeId = null)
    {
        var pa = PlakaAnahtari(plaka);
        using var con = _depo.YeniBaglanti();
        using var cmd = con.CreateCommand();
        cmd.CommandText = bolgeId.HasValue
            ? "SELECT COALESCE(SUM(kalan),0) FROM anlik_borc WHERE plaka_anahtar=$pa AND bolge_id=$b"
            : "SELECT COALESCE(SUM(kalan),0) FROM anlik_borc WHERE plaka_anahtar=$pa";
        cmd.Parameters.AddWithValue("$pa", pa);
        if (bolgeId.HasValue) cmd.Parameters.AddWithValue("$b", bolgeId.Value);
        return Convert.ToDecimal(cmd.ExecuteScalar());
    }

    public sealed class AbonelikSonuc { public long AbonelikId; public string TarifeAdi = ""; public DateTime? Bitis; public bool Ucretli; public bool BuBolgedeGecerli; }

    /// <summary>Aktif abonelik var mı? bolgeId verilirse yalnız o bölgeye bağlı (veya bölgesiz "[Kapalı]" tipi) sayılır.</summary>
    public AbonelikSonuc? AktifAbonelik(string plaka, long bolgeId)
    {
        var pa = PlakaAnahtari(plaka);
        var simdi = DateTime.Now;
        using var con = _depo.YeniBaglanti();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT abonelik_id, tarife_adi, bitis, bolgeler_json, ucretli FROM anlik_abonelik WHERE plaka_anahtar=$pa AND baslangic<=$s AND (bitis IS NULL OR bitis>=$s)";
        cmd.Parameters.AddWithValue("$pa", pa);
        cmd.Parameters.AddWithValue("$s", simdi.ToString("O"));
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var bolgelerJson = r.IsDBNull(3) ? "[]" : r.GetString(3);
            var bolgeler = JsonSerializer.Deserialize<List<long>>(bolgelerJson) ?? new();
            bool buBolgede = bolgeler.Count == 0 || bolgeler.Contains(bolgeId);
            if (!buBolgede) continue;

            return new AbonelikSonuc
            {
                AbonelikId = r.GetInt64(0),
                TarifeAdi = r.IsDBNull(1) ? "" : r.GetString(1),
                Bitis = r.IsDBNull(2) ? null : DateTime.Parse(r.GetString(2)),
                Ucretli = r.GetInt32(4) == 1,
                BuBolgedeGecerli = true
            };
        }
        return null;
    }

    public sealed class AracKartiSonuc { public long AracId; public long AracTipiId; public long TarifeId; public long? MusteriFirmaId; }

    public AracKartiSonuc? AracBul(string plaka)
    {
        var pa = PlakaAnahtari(plaka);
        using var con = _depo.YeniBaglanti();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT arac_id, arac_tipi_id, tarife_id, musteri_firma_id FROM anlik_arac WHERE plaka_anahtar=$pa LIMIT 1";
        cmd.Parameters.AddWithValue("$pa", pa);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new AracKartiSonuc
        {
            AracId = r.GetInt64(0),
            AracTipiId = r.GetInt64(1),
            TarifeId = r.GetInt64(2),
            MusteriFirmaId = r.IsDBNull(3) ? null : r.GetInt64(3)
        };
    }

    public sealed class TarifeSonuc { public long TarifeDetayId; public int UcretsizDakika; public int OpsiyonelSure; public List<(int bas, int bit, decimal fiyat)> Kademeler = new(); }

    /// <summary>Bölge + araç tipi için tarife detayını bulur (VehicleParkManager.GetParkPriceDetayli ile aynı eşleme).</summary>
    public TarifeSonuc? TarifeBul(long bolgeId, long aracTipiId, int yil)
    {
        using var con = _depo.YeniBaglanti();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT tarife_detay_id, ucretsiz_dakika, opsiyonel_sure, arac_tipleri_json, kademeler_json FROM anlik_tarife WHERE bolge_id=$b AND yil=$y";
        cmd.Parameters.AddWithValue("$b", bolgeId);
        cmd.Parameters.AddWithValue("$y", yil);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var aracTipleri = JsonSerializer.Deserialize<List<long>>(r.GetString(3)) ?? new();
            if (!aracTipleri.Contains(aracTipiId)) continue;

            var kademelerRaw = JsonSerializer.Deserialize<List<Dictionary<string, JsonElement>>>(r.GetString(4)) ?? new();
            var sonuc = new TarifeSonuc { TarifeDetayId = r.GetInt64(0), UcretsizDakika = r.GetInt32(1), OpsiyonelSure = r.GetInt32(2) };
            foreach (var k in kademelerRaw)
                sonuc.Kademeler.Add((k["BaslangicDk"].GetInt32(), k["BitisDk"].GetInt32(), k["Fiyat"].GetDecimal()));
            return sonuc;
        }
        return null;
    }

    public int KapasiteOku() => MetaOkuInt("kapasite", 0);
    public long OtoAracTipiOku() => MetaOkuInt("oto_arac_tipi", 0);
    public long OtoTarifeOku() => MetaOkuInt("oto_tarife", 0);
    public bool OfflineIzinliOku() => MetaOkuInt("offline_izinli", 1) == 1;

    public List<long> EngelliTipleriOku()
    {
        var json = MetaOkuString("engelli_tipler");
        return string.IsNullOrEmpty(json) ? new() : (JsonSerializer.Deserialize<List<long>>(json) ?? new());
    }

    public int IcerideAracSayisi()
    {
        using var con = _depo.YeniBaglanti();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM anlik_giris";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private string? MetaOkuString(string anahtar)
    {
        using var con = _depo.YeniBaglanti();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT deger FROM anlik_meta WHERE anahtar=$a";
        cmd.Parameters.AddWithValue("$a", anahtar);
        return cmd.ExecuteScalar() as string;
    }

    private int MetaOkuInt(string anahtar, int varsayilan)
    {
        var s = MetaOkuString(anahtar);
        return int.TryParse(s, out var v) ? v : varsayilan;
    }
}
