using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Otopark.Core.Offline;

/// <summary>
/// ÇEVRİMDIŞI İŞLEM KUYRUĞU (20.09.2026). Bkz. PLAN_OFFLINE_CALISMA.md Bölüm 4, 7.4.
///
/// K1: Ekle() kalemi SQLite'a WAL+FULL senkron ile YAZAR ve TAMAMLANDIKTAN SONRA döner —
/// çağıran taraf (bariyer açma) bu satırdan sonrasını çalıştırmalıdır.
/// K3: sıra sira_no ile korunur; bagli_islem_no TAMAM olmadan bir kalem gönderilmez.
/// K5: TAMAM/RED hiçbir satır silinmez (bu tablo yerelde de kalıcıdır; sunucudaki
///     OFFLINE_ISLEM ile birlikte çift kayıt sağlar).
/// </summary>
public sealed class IslemKuyrugu
{
    private readonly YerelDepo _depo;
    private readonly SahaOfflineClient _client;
    private readonly string _cihazId;
    private readonly Action<string, string>? _olayLogu;
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public IslemKuyrugu(YerelDepo depo, SahaOfflineClient client, string cihazId, Action<string, string>? olayLogu = null)
    {
        _depo = depo;
        _client = client;
        _cihazId = cihazId;
        _olayLogu = olayLogu;
    }

    /// <summary>Yeni kalem ekler ve İşlem numarasını döner. Diskte kalıcı olmadan DÖNMEZ.</summary>
    public string Ekle(string tur, string plaka, long companyId, long zoneId, long kullaniciId, DateTime olayZamani,
                        object govde, string? yerelGirisId = null, string? bagliIslemNo = null, string kaynak = "MASAUSTU")
    {
        var islemNo = Guid.NewGuid().ToString("N");
        var simdi = DateTime.Now;

        using var con = _depo.YeniBaglanti();
        using var cmd = con.CreateCommand();
        cmd.CommandText = @"INSERT INTO islem_kuyrugu
            (islem_no, tur, plaka, yerel_giris_id, bagli_islem_no, olay_zamani, olay_zamani_duzeltilmis,
             kaynak, cihaz_id, kullanici_id, company_id, zone_id, govde_json, durum, olusturma)
            VALUES ($no,$tur,$plaka,$ygid,$bagli,$oz,$ozd,$kaynak,$cid,$kid,$comp,$zone,$govde,'BEKLIYOR',$olz)";
        cmd.Parameters.AddWithValue("$no", islemNo);
        cmd.Parameters.AddWithValue("$tur", tur);
        cmd.Parameters.AddWithValue("$plaka", plaka);
        cmd.Parameters.AddWithValue("$ygid", (object?)yerelGirisId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$bagli", (object?)bagliIslemNo ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$oz", olayZamani.ToString("O"));
        cmd.Parameters.AddWithValue("$ozd", olayZamani.ToString("O")); // saat kayması düzeltmesi Faz sonrası eklenebilir (K8 metni)
        cmd.Parameters.AddWithValue("$kaynak", kaynak);
        cmd.Parameters.AddWithValue("$cid", _cihazId);
        cmd.Parameters.AddWithValue("$kid", kullaniciId);
        cmd.Parameters.AddWithValue("$comp", companyId);
        cmd.Parameters.AddWithValue("$zone", zoneId);
        cmd.Parameters.AddWithValue("$govde", JsonSerializer.Serialize(govde));
        cmd.ExecuteNonQuery();

        _depo.OlayYaz("KUYRUK_EKLE", $"{tur} {plaka} islemNo={islemNo}");
        return islemNo;
    }

    public int BekleyenSayisi() => Say("BEKLIYOR");
    public int RedSayisi() => Say("RED");

    private int Say(string durum)
    {
        using var con = _depo.YeniBaglanti();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM islem_kuyrugu WHERE durum=$d";
        cmd.Parameters.AddWithValue("$d", durum);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public DateTime? EnEskiBekleyenTarihi()
    {
        using var con = _depo.YeniBaglanti();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT MIN(olusturma) FROM islem_kuyrugu WHERE durum='BEKLIYOR'";
        var s = cmd.ExecuteScalar() as string;
        return s == null ? null : DateTime.Parse(s);
    }

    public sealed class BekleyenSatir { public string IslemNo = ""; public string Tur = ""; public string Plaka = ""; public string? YerelGirisId; public string? BagliIslemNo; public string GovdeJson = ""; public DateTime OlayZamani; public long CompanyId; public long ZoneId; public long KullaniciId; public string Kaynak = ""; }

    /// <summary>
    /// SIRA + BAĞIMLILIK GÖZETEREK gönderilebilecek kalemleri döner (en fazla adet kadar).
    /// bagli_islem_no dolu ve o kalem henüz TAMAM değilse bu kalem ATLANIR (K3).
    /// </summary>
    public List<BekleyenSatir> GonderilecekleriGetir(int adet)
    {
        var sonuc = new List<BekleyenSatir>();
        using var con = _depo.YeniBaglanti();
        using var cmd = con.CreateCommand();
        cmd.CommandText = @"SELECT islem_no, tur, plaka, yerel_giris_id, bagli_islem_no, govde_json, olay_zamani, company_id, zone_id, kullanici_id, kaynak
                             FROM islem_kuyrugu WHERE durum='BEKLIYOR' ORDER BY sira_no ASC, rowid ASC";
        using var r = cmd.ExecuteReader();
        while (r.Read() && sonuc.Count < adet)
        {
            var bagli = r.IsDBNull(4) ? null : r.GetString(4);
            if (!string.IsNullOrEmpty(bagli) && !BagliTamamMi(con, bagli)) continue;

            sonuc.Add(new BekleyenSatir
            {
                IslemNo = r.GetString(0),
                Tur = r.GetString(1),
                Plaka = r.GetString(2),
                YerelGirisId = r.IsDBNull(3) ? null : r.GetString(3),
                BagliIslemNo = bagli,
                GovdeJson = r.GetString(5),
                OlayZamani = DateTime.Parse(r.GetString(6)),
                CompanyId = r.GetInt64(7),
                ZoneId = r.GetInt64(8),
                KullaniciId = r.GetInt64(9),
                Kaynak = r.GetString(10)
            });
        }
        return sonuc;
    }

    private bool BagliTamamMi(SqliteConnection con, string bagliIslemNo)
    {
        using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT durum FROM islem_kuyrugu WHERE islem_no=$n";
        cmd.Parameters.AddWithValue("$n", bagliIslemNo);
        var durum = cmd.ExecuteScalar() as string;
        return durum == "TAMAM";
    }

    private void DurumGuncelle(string islemNo, string durum, string? mesaj, long? sunucuEntryId, string? sunucuYanitiJson)
    {
        using var con = _depo.YeniBaglanti();
        using var cmd = con.CreateCommand();
        cmd.CommandText = @"UPDATE islem_kuyrugu SET durum=$d, son_hata=$h, sunucu_entry_id=$se, sunucu_yaniti_json=$sy,
                             son_deneme=$sd, deneme=deneme+1, tamamlanma=CASE WHEN $d IN ('TAMAM','RED') THEN $sd ELSE tamamlanma END
                             WHERE islem_no=$n";
        cmd.Parameters.AddWithValue("$d", durum);
        cmd.Parameters.AddWithValue("$h", (object?)mesaj ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$se", (object?)sunucuEntryId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$sy", (object?)sunucuYanitiJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$sd", DateTime.Now.ToString("O"));
        cmd.Parameters.AddWithValue("$n", islemNo);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Bir GİRİŞ kaleminin sunucu giriş kimliğini yerel_giris_id üzerinden bulur (KAPALI_CIKIS/BORC_ODEME için).</summary>
    public long? SunucuEntryIdBul(string yerelGirisId)
    {
        using var con = _depo.YeniBaglanti();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT sunucu_entry_id FROM islem_kuyrugu WHERE yerel_giris_id=$g AND tur='GIRIS' AND durum='TAMAM' AND sunucu_entry_id IS NOT NULL ORDER BY rowid DESC LIMIT 1";
        cmd.Parameters.AddWithValue("$g", yerelGirisId);
        var v = cmd.ExecuteScalar();
        return v == null || v is DBNull ? null : Convert.ToInt64(v);
    }

    /// <summary>
    /// Sunucuya bağlıyken çağrılır: gönderilebilecek kalemleri (K3 sırasıyla) sunucuya iletir.
    /// Döner: gönderilen kalem sayısı (0 ise kuyruk boş demektir).
    /// </summary>
    public async Task<int> SenkronizeEtAsync(CancellationToken ct = default)
    {
        var kalemler = GonderilecekleriGetir(20);
        if (kalemler.Count == 0) return 0;

        var istek = new OfflineSenkronIstek
        {
            CihazId = _cihazId,
            Kalemler = kalemler.Select(k => new OfflineIslemKalemi
            {
                IslemNo = k.IslemNo,
                Tur = k.Tur,
                OlayZamani = k.OlayZamani,
                OlayZamaniDuzeltilmis = k.OlayZamani,
                Kaynak = k.Kaynak,
                CihazId = _cihazId,
                KullaniciId = k.KullaniciId,
                CompanyId = k.CompanyId,
                ZoneId = k.ZoneId,
                Plaka = k.Plaka,
                YerelGirisId = k.YerelGirisId,
                BagliIslemNo = k.BagliIslemNo,
                GovdeJson = k.GovdeJson
            }).ToList()
        };

        var yanit = await _client.SenkronAsync(istek, ct);
        if (yanit == null)
        {
            _olayLogu?.Invoke("SENKRON_HATA", "Sunucudan yanıt alınamadı; kalemler BEKLIYOR kalıyor.");
            return 0;
        }

        foreach (var sonuc in yanit.Sonuclar)
        {
            var yaniJson = JsonSerializer.Serialize(sonuc);
            DurumGuncelle(sonuc.IslemNo, sonuc.Durum, sonuc.Mesaj, sonuc.SunucuEntryId, yaniJson);
            _olayLogu?.Invoke("SENKRON_SONUC", $"{sonuc.IslemNo} -> {sonuc.Durum} {sonuc.Mesaj}");
        }

        return kalemler.Count;
    }

    /// <summary>Açılışta çağrılır: GONDERILIYOR gibi ara durumda kalmış satır YOK (K6) — bu tasarımda
    /// zaten yalnız BEKLIYOR/TAMAM/RED durumları kullanıldığı için ek işlem gerekmez, yine de
    /// bütünlük için burada bırakıldı.</summary>
    public void AcilistaKurtar()
    {
        using var con = _depo.YeniBaglanti();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "UPDATE islem_kuyrugu SET durum='BEKLIYOR' WHERE durum='GONDERILIYOR'";
        cmd.ExecuteNonQuery();
    }
}
