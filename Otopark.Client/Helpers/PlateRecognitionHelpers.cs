using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http.Headers;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.IO;

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

    internal sealed class PlateRecognizerClient
    {
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        private readonly string _token;

        public PlateRecognizerClient(string token) { _token = token; }

        /// <summary>
        /// Plaka tanima yapar. regions bos birakilirsa tum bolgeler denenir (yabanci plaka dahil).
        /// Birden fazla aday donerse IsLikelyPlate gecen en yuksek skorlu aday secilir.
        /// </summary>
        public async Task<PlateRecognitionResult?> RecognizeAsync(string imagePath, string? regions, CancellationToken ct)
        {
            if (!File.Exists(imagePath)) return null;

            using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.platerecognizer.com/v1/plate-reader/");
            req.Headers.Authorization = new AuthenticationHeaderValue("Token", _token);

            using var form = new MultipartFormDataContent();
            if (!string.IsNullOrWhiteSpace(regions))
                form.Add(new StringContent(regions), "regions");

            var bytes = await File.ReadAllBytesAsync(imagePath, ct);
            form.Add(new ByteArrayContent(bytes), "upload", Path.GetFileName(imagePath));

            req.Content = form;

            using var resp = await Http.SendAsync(req, ct);
            var json = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode) return null;

            var parsed = JsonConvert.DeserializeObject<PlateRecognizerResponse>(json);
            if (parsed?.results == null || parsed.results.Length == 0) return null;

            // Tum sonuclar ve adaylar icinden gecerli format olan en yuksek skorluyu sec
            (string plate, double score)? best = null;
            foreach (var r in parsed.results)
            {
                if (r.candidates == null) continue;
                foreach (var c in r.candidates)
                {
                    if (string.IsNullOrWhiteSpace(c.plate)) continue;
                    var normalized = PlateRules.Normalize(c.plate);
                    if (!PlateRules.IsLikelyPlate(normalized)) continue;
                    if (best == null || c.score > best.Value.score)
                        best = (normalized, c.score);
                }
            }

            if (best == null)
            {
                // Format gecmeyen de olsa en yuksek skorluyu don (cagiran tarafta bir daha filtrelenir)
                var topR = parsed.results[0];
                if (topR.candidates != null && topR.candidates.Length > 0)
                {
                    var topC = topR.candidates.OrderByDescending(c => c.score).First();
                    return new PlateRecognitionResult(topC.plate ?? "", topC.score);
                }
                return null;
            }

            return new PlateRecognitionResult(best.Value.plate, best.Value.score);
        }

        internal sealed class PlateRecognitionResult
        {
            public string Plate { get; }
            public double Score { get; }
            public PlateRecognitionResult(string plate, double score) { Plate = plate; Score = score; }
        }

        private sealed class PlateRecognizerResponse { public PlateRecognizerResult[]? results { get; set; } }
        private sealed class PlateRecognizerResult { public PlateCandidate[]? candidates { get; set; } }
        private sealed class PlateCandidate { public string? plate { get; set; } public double score { get; set; } }
    }
}
