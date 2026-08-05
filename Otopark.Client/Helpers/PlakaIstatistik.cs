using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Otopark.Client.Helpers
{
    /// <summary>
    /// FAZ 5 - IZLEME.
    /// Plaka tanimanin gunluk saglik ozetini tutar ve gun degisince diske yazar.
    ///
    /// AMAC: kamera acisi bozuldugunda ya da tanima kalitesi dustugunde bunu
    /// aylar sonra degil ERTESI GUN gormek. Ozellikle "kenarda" orani kritik:
    /// olculdu ki plaka kutusu kare kenarina degdiginde dogruluk %100'den %50'ye
    /// dusuyor. Bu oran yukseliyorsa kamera kaymis/egilmis demektir.
    ///
    /// Dosya: C:\Otopark\plaka_ozet.log  (gunde bir satir, insan okuyabilir)
    /// </summary>
    internal static class PlakaIstatistik
    {
        private static readonly string OzetYolu = @"C:\Otopark\plaka_ozet.log";
        private static readonly object _kilit = new();

        private static DateTime _gun = DateTime.Today;
        private static int _toplam;
        private static int _supheli;
        private static int _kenarda;
        private static readonly Dictionary<int, int> _bolgeler = new();

        /// <summary>Bir plaka okumasi sonuclandiginda cagrilir.</summary>
        public static void Kaydet(bool supheli, bool kenarda, int regionId)
        {
            lock (_kilit)
            {
                if (DateTime.Today != _gun) YazVeSifirla();

                _toplam++;
                if (supheli) _supheli++;
                if (kenarda) _kenarda++;
                if (regionId >= 0)
                    _bolgeler[regionId] = _bolgeler.TryGetValue(regionId, out var s) ? s + 1 : 1;
            }
        }

        /// <summary>Uygulama kapanirken cagrilir - biriken gun kaybolmasin.</summary>
        public static void Bitir()
        {
            lock (_kilit)
            {
                if (_toplam > 0) YazVeSifirla();
            }
        }

        // _kilit ALTINDA cagrilir
        private static void YazVeSifirla()
        {
            try
            {
                if (_toplam > 0)
                {
                    double supheliYuzde = 100.0 * _supheli / _toplam;
                    double kenardaYuzde = 100.0 * _kenarda / _toplam;

                    var bolgeMetni = string.Join(" ", _bolgeler
                        .OrderByDescending(kv => kv.Value)
                        .Take(6)
                        .Select(kv => $"{BolgeAdi(kv.Key)}:{kv.Value}"));

                    var satir =
                        $"{_gun:yyyy-MM-dd} | okuma={_toplam} " +
                        $"otomatik={_toplam - _supheli} supheli={_supheli} (%{supheliYuzde:F0}) " +
                        $"kenarda={_kenarda} (%{kenardaYuzde:F0}) | {bolgeMetni}";

                    // Kamera acisi uyarisi: kenarda orani belirgin yukseldiyse dikkat cek
                    if (kenardaYuzde >= 25 && _toplam >= 10)
                        satir += "  *** UYARI: kenarda orani yuksek - KAMERA ACISINI KONTROL EDIN ***";

                    Directory.CreateDirectory(Path.GetDirectoryName(OzetYolu)!);
                    File.AppendAllText(OzetYolu, satir + Environment.NewLine);
                }
            }
            catch { /* ozet yazilamamasi tanimayi durdurmamali */ }

            _gun = DateTime.Today;
            _toplam = 0;
            _supheli = 0;
            _kenarda = 0;
            _bolgeler.Clear();
        }

        /// <summary>
        /// fast-plate-ocr global modelinin bolge sinif numaralari.
        /// Resmi bir liste yayinlanmadigi icin bunlar KENDI VERIMIZDE gozlemlenerek
        /// dogrulanmis eslesmelerdir; bilinmeyenler ham numara olarak yazilir.
        /// </summary>
        private static string BolgeAdi(int id) => id switch
        {
            60 => "TR",
            23 => "DE",
            9 => "BE",
            21 => "FR",
            5 => "AT",
            43 => "NL",
            _ => $"#{id}",
        };
    }
}
