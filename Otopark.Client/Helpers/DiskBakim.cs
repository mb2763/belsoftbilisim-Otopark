using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Otopark.Client.Helpers
{
    /// <summary>
    /// Disk bakimi: eski gun klasorlerini ve sisen log dosyalarini temizler.
    ///
    /// NEDEN: uygulamanin yazdigi hicbir klasorde saklama suresi yoktu.
    ///   D:\GESI\OTOPARK\Entry\yyyy\MM\dd\   - kamera kareleri (gunde ~40.000+ dosya)
    ///   D:\GESI\OTOPARK\Exit\yyyy\MM\dd\
    ///   C:\Otopark\VehicleFrames\yyyy-MM-dd\ - plaka kutusu bulunan kareler
    ///                                          (olculdu: 1 gunde 1.933 dosya / 425 MB)
    ///   C:\Otopark\log.txt                   - sinirsiz append
    /// Disk dolunca TUM sistem yavaslar; ayrica bu klasorler tarandigi icin
    /// (bkz. CameraSnapshotService.CleanupOldFiles) buyudukce her islem uzar.
    ///
    /// Tum islemler sessizdir: basarisiz olurlarsa uygulama normal calismaya devam eder.
    /// </summary>
    internal static class DiskBakim
    {
        /// <summary>Kamera kareleri ve plaka kareleri icin saklama suresi (gun).</summary>
        public const int SaklamaGunu = 14;

        /// <summary>Log dosyasi bu boyutu asinca dondurulur.</summary>
        private const long LogTavanByte = 10 * 1024 * 1024;

        /// <summary>
        /// Uygulama acilisinda bir kez cagrilir. Beklenmemelidir (fire-and-forget).
        /// </summary>
        public static Task TumBakimAsync()
        {
            return Task.Run(() =>
            {
                GunKlasorleriniTemizle(@"D:\GESI\OTOPARK\Entry", SaklamaGunu);
                GunKlasorleriniTemizle(@"D:\GESI\OTOPARK\Exit", SaklamaGunu);
                GunKlasorleriniTemizle(@"C:\Otopark\VehicleFrames", SaklamaGunu);
                LogDondur(@"C:\Otopark\log.txt");
            });
        }

        /// <summary>
        /// Kok klasor altindaki GUN klasorlerinden <paramref name="gunSayisi"/> gunden
        /// eski olanlari siler.
        ///
        /// Iki yapiyi da destekler:
        ///   kok\yyyy\MM\dd      (kamera kareleri)
        ///   kok\yyyy-MM-dd      (VehicleFrames)
        ///
        /// Tarih KLASOR ADINDAN cozulur; dosya sistemi zaman damgasina guvenilmez
        /// (kopyalama/yedekleme damgayi degistirir).
        /// </summary>
        public static void GunKlasorleriniTemizle(string kok, int gunSayisi)
        {
            try
            {
                if (!Directory.Exists(kok)) return;
                var sinir = DateTime.Today.AddDays(-gunSayisi);

                foreach (var gunKlasoru in GunKlasorleriniBul(kok))
                {
                    if (gunKlasoru.Tarih >= sinir) continue;
                    try
                    {
                        Directory.Delete(gunKlasoru.Yol, recursive: true);
                        Log($"Disk bakimi: silindi {gunKlasoru.Yol} ({gunKlasoru.Tarih:yyyy-MM-dd})");
                    }
                    catch (Exception ex)
                    {
                        Log($"Disk bakimi: silinemedi {gunKlasoru.Yol} -> {ex.Message}");
                    }
                }

                // Bosalan yil/ay klasorlerini de topla
                BosKlasorleriTopla(kok);
            }
            catch (Exception ex) { Log($"Disk bakimi hatasi ({kok}): {ex.Message}"); }
        }

        private static System.Collections.Generic.IEnumerable<(string Yol, DateTime Tarih)> GunKlasorleriniBul(string kok)
        {
            var sonuc = new System.Collections.Generic.List<(string, DateTime)>();

            foreach (var seviye1 in GuvenliAltKlasorler(kok))
            {
                var ad1 = Path.GetFileName(seviye1);

                // Yapi 2: kok\yyyy-MM-dd
                if (DateTime.TryParseExact(ad1, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var tarih1))
                {
                    sonuc.Add((seviye1, tarih1));
                    continue;
                }

                // Yapi 1: kok\yyyy\MM\dd
                if (!int.TryParse(ad1, out var yil) || yil < 2000 || yil > 2100) continue;

                foreach (var seviye2 in GuvenliAltKlasorler(seviye1))
                {
                    if (!int.TryParse(Path.GetFileName(seviye2), out var ay) || ay < 1 || ay > 12) continue;

                    foreach (var seviye3 in GuvenliAltKlasorler(seviye2))
                    {
                        if (!int.TryParse(Path.GetFileName(seviye3), out var gun) || gun < 1 || gun > 31) continue;
                        try { sonuc.Add((seviye3, new DateTime(yil, ay, gun))); }
                        catch { /* gecersiz tarih (orn. 31 Subat) - atla */ }
                    }
                }
            }

            return sonuc;
        }

        private static string[] GuvenliAltKlasorler(string yol)
        {
            try { return Directory.GetDirectories(yol); }
            catch { return Array.Empty<string>(); }
        }

        /// <summary>Icinde dosya ve alt klasor kalmayan yil/ay klasorlerini siler.</summary>
        private static void BosKlasorleriTopla(string kok)
        {
            foreach (var seviye1 in GuvenliAltKlasorler(kok))
            {
                foreach (var seviye2 in GuvenliAltKlasorler(seviye1))
                    SilBossa(seviye2);
                SilBossa(seviye1);
            }
        }

        private static void SilBossa(string klasor)
        {
            try
            {
                if (Directory.EnumerateFileSystemEntries(klasor).Any()) return;
                Directory.Delete(klasor);
            }
            catch { }
        }

        /// <summary>
        /// Log dosyasi tavani asarsa tek bir yedege dondurur (log_eski.txt).
        /// Eskiden dosya sinirsiz buyuyordu.
        /// </summary>
        public static void LogDondur(string logYolu)
        {
            try
            {
                var fi = new FileInfo(logYolu);
                if (!fi.Exists || fi.Length < LogTavanByte) return;

                var eski = Path.Combine(
                    Path.GetDirectoryName(logYolu)!,
                    Path.GetFileNameWithoutExtension(logYolu) + "_eski" + Path.GetExtension(logYolu));

                try { if (File.Exists(eski)) File.Delete(eski); } catch { }
                File.Move(logYolu, eski);
                Log($"Disk bakimi: log dondurudu ({fi.Length / 1024 / 1024} MB) -> {Path.GetFileName(eski)}");
            }
            catch { }
        }

        private static void Log(string mesaj)
        {
            try
            {
                File.AppendAllText(@"C:\Otopark\log.txt",
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} | {mesaj}{Environment.NewLine}");
            }
            catch { }
        }
    }
}
