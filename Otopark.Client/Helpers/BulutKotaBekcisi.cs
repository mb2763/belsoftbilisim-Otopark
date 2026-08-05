using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Otopark.Client.Helpers
{
    /// <summary>
    /// Bulut plaka tanima (PlateRecognizer) cagrilarini KOTA icinde tutar.
    ///
    /// NEDEN GEREKLI - olculdu (1388 gercek kare, ~5 saatlik trafik):
    ///     emin okuma     : 138 kare
    ///     SUPHELI okuma  :  37 kare
    ///     hic okunamayan :   4 kare
    /// Supheli + okunamayan = 41 kare. Her birine bulut sorulsa gunde ~98,
    /// ayda ~2950 sorgu eder; ucretsiz kota 2500/AY. Yani kota tasar.
    ///
    /// AMA o 37 supheli kare aslinda sadece ~7 ARACA aittir - ayni plaka arka arkaya
    /// gelen karelerde tekrar tekrar sorulmaktadir. Ayni plakayi kisa sure icinde
    /// yeniden sormanin hicbir faydasi yok (ayni goruntu, ayni cevap).
    ///
    /// Bu sinif iki koruma uygular:
    ///   1) AYNI PLAKA TEKRARI  : ayni okuma AyniPlakaSaniye icinde bir kez sorulur
    ///   2) SAATLIK TAVAN       : ne olursa olsun saatte SaatlikTavan sorguyu asmaz
    /// Sonuc: ~7 sorgu / 5 saat -> ayda ~500, kotanin cok altinda.
    ///
    /// Okunamayan kareler icin plaka metni olmadigindan anahtar olarak "?" kullanilir;
    /// onlar da ayni tekrar korumasina tabidir.
    /// </summary>
    internal static class BulutKotaBekcisi
    {
        private const int AyniPlakaSaniye = 60;
        private const int SaatlikTavan = 40;

        private static readonly object _kilit = new();
        private static readonly Dictionary<string, DateTime> _sonSorgu = new(StringComparer.OrdinalIgnoreCase);
        private static readonly List<DateTime> _sonBirSaat = new();

        /// <summary>
        /// Bu okuma icin buluta sorulabilir mi? Izin verilirse sayac ISLENIR
        /// (yani cagiran taraf gercekten sorgu yapmalidir).
        /// </summary>
        public static bool IzinVar(string? plaka)
        {
            var anahtar = string.IsNullOrWhiteSpace(plaka) ? "?" : plaka.Trim();
            var simdi = DateTime.UtcNow;

            lock (_kilit)
            {
                // 1) Ayni plaka kisa sure once soruldu mu?
                if (_sonSorgu.TryGetValue(anahtar, out var son) &&
                    (simdi - son).TotalSeconds < AyniPlakaSaniye)
                {
                    return false;
                }

                // 2) Saatlik tavan
                _sonBirSaat.RemoveAll(t => (simdi - t).TotalHours >= 1);
                if (_sonBirSaat.Count >= SaatlikTavan)
                {
                    Log($"Bulut kota tavani: son 1 saatte {_sonBirSaat.Count} sorgu yapildi, " +
                        $"'{anahtar}' icin sorulmuyor.");
                    return false;
                }

                _sonSorgu[anahtar] = simdi;
                _sonBirSaat.Add(simdi);

                // Sozluk sinirsiz buyumesin
                if (_sonSorgu.Count > 500)
                {
                    var eskiler = _sonSorgu
                        .Where(kv => (simdi - kv.Value).TotalMinutes > 30)
                        .Select(kv => kv.Key)
                        .ToList();
                    foreach (var k in eskiler) _sonSorgu.Remove(k);
                }

                return true;
            }
        }

        /// <summary>Son 1 saatteki sorgu sayisi (log/tani icin).</summary>
        public static int SonBirSaatteki
        {
            get
            {
                lock (_kilit)
                {
                    var simdi = DateTime.UtcNow;
                    _sonBirSaat.RemoveAll(t => (simdi - t).TotalHours >= 1);
                    return _sonBirSaat.Count;
                }
            }
        }

        private static void Log(string msg)
        {
            try
            {
                File.AppendAllText(@"C:\Otopark\log.txt",
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} | {msg}{Environment.NewLine}");
            }
            catch { }
        }
    }
}
