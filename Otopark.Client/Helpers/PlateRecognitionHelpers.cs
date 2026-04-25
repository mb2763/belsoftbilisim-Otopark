using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace Otopark.Client.Helpers
{
    internal sealed class StablePlate
    {
        public string Plate { get; }
        public double Score { get; }
        public StablePlate(string plate, double score) { Plate = plate; Score = score; }
    }

    internal sealed class PlateStabilizer
    {
        private readonly double _minScore;
        private readonly double _windowSeconds;
        private readonly int _neededHits;
        private readonly List<(string Plate, double Score, DateTime Ts)> _buffer = new();

        public PlateStabilizer(double minScore, double windowSeconds, int neededHits)
        { _minScore = minScore; _windowSeconds = windowSeconds; _neededHits = neededHits; }

        public StablePlate? Push(string plate, double score, DateTime utcNow)
        {
            if (score < _minScore) return null;

            // Yuksek guvenli sonuclar tek hit'de kabul edilir (hareketli arac icin kritik)
            if (score >= 0.85)
            {
                _buffer.Clear();
                return new StablePlate(plate, score);
            }

            _buffer.Add((plate, score, utcNow));
            var cutoff = utcNow.AddSeconds(-_windowSeconds);
            _buffer.RemoveAll(x => x.Ts < cutoff);

            if (_buffer.Count(x => x.Plate == plate && x.Score >= _minScore) < _neededHits)
                return null;

            var avg = _buffer.Where(x => x.Plate == plate).Average(x => x.Score);
            _buffer.Clear();
            return new StablePlate(plate, avg);
        }
    }

    internal sealed class DuplicateSuppressor
    {
        private readonly double _suppressSeconds;
        private readonly Dictionary<string, DateTime> _lastSent = new();

        public DuplicateSuppressor(double suppressSeconds) { _suppressSeconds = suppressSeconds; }

        public bool ShouldSuppress(string plate, DateTime utcNow)
        {
            if (_lastSent.TryGetValue(plate, out var last) &&
                (utcNow - last).TotalSeconds < _suppressSeconds)
                return true;

            _lastSent[plate] = utcNow;

            var oldCutoff = utcNow.AddMinutes(-10);
            foreach (var k in _lastSent.Where(kv => kv.Value < oldCutoff).Select(kv => kv.Key).ToList())
                _lastSent.Remove(k);

            return false;
        }
    }

    internal static class PlateRules
    {
        private static readonly Regex TrPlate =
            new Regex(@"^(0[1-9]|[1-7][0-9]|8[01])[A-Z]{1,3}[0-9]{2,4}$", RegexOptions.Compiled);

        // OCR tarafindan plaka sanilabilecek yaygin tabela/yazi kelimeleri
        private static readonly HashSet<string> BlacklistWords = new(StringComparer.OrdinalIgnoreCase)
        {
            "GIRIS", "CIKIS", "GIRISCIKIS", "CIKISGIRIS",
            "DUR", "STOP", "PARK", "OTOPARK", "OTOPAK", "OTOPAR",
            "YASAK", "YAVAS", "DIKKAT", "TAKSI", "OKUL",
            "SERBEST", "KAPALI", "ACIK", "DOLU", "BOS",
            "VIP", "TAXI", "BUS", "TRUCK", "KAT", "MERKEZ",
            "IN", "OUT", "ENTER", "EXIT", "ENTRANCE",
            "KIOSK", "KASA", "VEZNE", "ODEME",
        };

        public static string Normalize(string plate)
        {
            if (string.IsNullOrWhiteSpace(plate)) return "";
            plate = plate.Trim().ToUpperInvariant();
            plate = new string(plate.Where(char.IsLetterOrDigit).ToArray());
            plate = plate.Replace('İ', 'I').Replace('Ö', 'O').Replace('Ü', 'U')
                         .Replace('Ş', 'S').Replace('Ç', 'C').Replace('Ğ', 'G');
            return plate;
        }

        public static bool IsLikelyTurkishPlate(string plate) =>
            !string.IsNullOrWhiteSpace(plate) && TrPlate.IsMatch(plate);

        /// <summary>
        /// Hem Turk hem yabanci plakalar icin genel gecerlilik kontrolu:
        /// - 5-10 karakter
        /// - Sadece harf+rakam
        /// - En az 2 harf ve 2 rakam
        /// - Yaygin tabela/yazi kelimeleri degil
        /// - Saf tekrar eden karakter (AAAAAA gibi) degil
        /// </summary>
        public static bool IsLikelyPlate(string plate)
        {
            if (string.IsNullOrWhiteSpace(plate)) return false;
            if (plate.Length < 5 || plate.Length > 10) return false;
            if (!plate.All(char.IsLetterOrDigit)) return false;

            int letterCount = plate.Count(char.IsLetter);
            int digitCount = plate.Count(char.IsDigit);
            if (letterCount < 2 || digitCount < 2) return false;

            // Tabela/yazi kelimesi kontrolu - plakadaki rakamlari cikarip sadece harfleri kontrol et
            var lettersOnly = new string(plate.Where(char.IsLetter).ToArray());
            if (BlacklistWords.Contains(lettersOnly)) return false;
            if (BlacklistWords.Contains(plate)) return false;

            // Tek karakterin tekrari (AAAAAA, 11111A gibi) - geçersiz
            if (plate.Distinct().Count() < 3) return false;

            return true;
        }
    }

}
