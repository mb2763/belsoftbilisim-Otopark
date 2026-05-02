using Newtonsoft.Json;
using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

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

            // Rakami harfe ceviren agresif kontrol (1→I, 0→O, 5→S, 8→B, 6→G, 2→Z)
            // "C1K1S" -> "CIKIS" yakalansin
            var letterized = new string(plate.Select(c => c switch
            {
                '0' => 'O', '1' => 'I', '5' => 'S', '8' => 'B', '6' => 'G', '2' => 'Z',
                _ => c
            }).ToArray());
            if (BlacklistWords.Contains(letterized)) return false;
            var letterizedLetters = new string(letterized.Where(char.IsLetter).ToArray());
            if (BlacklistWords.Contains(letterizedLetters)) return false;

            // Tek karakterin tekrari (AAAAAA, 11111A gibi) - geçersiz
            if (plate.Distinct().Count() < 3) return false;

            return true;
        }
    }

    /// <summary>
    /// PlateRecognizer Cloud API istemcisi - coklu token destekli.
    /// Bir token 403/429/402 dondurursa otomatik olarak diger tokene gecer.
    /// </summary>
    internal sealed class PlateRecognizerClient
    {
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };

        private static readonly SemaphoreSlim _rateLimiter = new(1, 1);
        private static DateTime _lastCallUtc = DateTime.MinValue;
        // PlateRecognizer free tier: 1 req/sec. Guvenli olsun diye 1.1 sn.
        private const int MinIntervalMs = 1100;

        private readonly List<string> _tokens;
        private int _activeIdx = 0;
        // Token'in son tukenme zamani (1 saat sonra tekrar denenir)
        private readonly Dictionary<string, DateTime> _tokenExhaustedAt = new();

        public PlateRecognizerClient(string token) : this(new[] { token }) { }

        public PlateRecognizerClient(IEnumerable<string> tokens)
        {
            _tokens = tokens?.Where(t => !string.IsNullOrWhiteSpace(t)).Distinct().ToList()
                      ?? new List<string>();
            if (_tokens.Count == 0)
                throw new ArgumentException("En az bir gecerli PlateRecognizer token verilmeli", nameof(tokens));
        }

        public int TokenCount => _tokens.Count;

        private string? PickActiveToken()
        {
            // Tukenmis tokenler 1 saat sonra tekrar denenir
            var cutoff = DateTime.UtcNow.AddHours(-1);
            for (int i = 0; i < _tokens.Count; i++)
            {
                int idx = (_activeIdx + i) % _tokens.Count;
                var tok = _tokens[idx];
                if (_tokenExhaustedAt.TryGetValue(tok, out var exhAt) && exhAt > cutoff)
                    continue; // hala tukenmis say
                _activeIdx = idx;
                return tok;
            }
            return null; // hepsi tukenmis
        }

        private void MarkExhausted(string token)
        {
            _tokenExhaustedAt[token] = DateTime.UtcNow;
            _activeIdx = (_activeIdx + 1) % _tokens.Count;
            AppLog($"Token tukendi, sonrakine geciliyor. Aktif token sayisi: {_tokens.Count - _tokenExhaustedAt.Count(kv => kv.Value > DateTime.UtcNow.AddHours(-1))}/{_tokens.Count}");
        }

        public async Task<PlateRecognitionResult?> RecognizeAsync(string imagePath, string? regions, CancellationToken ct)
        {
            if (!File.Exists(imagePath)) return null;

            // Rate limit: max ~1 cagri / 0.8sn (tum tokenler arasinda paylasimli)
            await _rateLimiter.WaitAsync(ct);
            try
            {
                var elapsed = (DateTime.UtcNow - _lastCallUtc).TotalMilliseconds;
                if (elapsed < MinIntervalMs)
                    await Task.Delay((int)(MinIntervalMs - elapsed), ct);
                _lastCallUtc = DateTime.UtcNow;
            }
            finally { _rateLimiter.Release(); }

            // Sirayla tokenleri dene; tukenmis olanlari atla
            Exception? lastEx = null;
            int rateRetries = 0;
            for (int attempt = 0; attempt < _tokens.Count + 2; attempt++)
            {
                var token = PickActiveToken();
                if (token == null) break; // hepsi tukenmis

                byte[] bytes;
                try
                {
                    if (!File.Exists(imagePath)) return null;
                    bytes = await File.ReadAllBytesAsync(imagePath, ct);
                }
                catch (FileNotFoundException) { return null; }
                catch (IOException) { return null; }

                using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.platerecognizer.com/v1/plate-reader/");
                req.Headers.Authorization = new AuthenticationHeaderValue("Token", token);

                using var form = new MultipartFormDataContent();
                if (!string.IsNullOrWhiteSpace(regions))
                    form.Add(new StringContent(regions), "regions");
                form.Add(new ByteArrayContent(bytes), "upload", Path.GetFileName(imagePath));
                req.Content = form;

                using var resp = await Http.SendAsync(req, ct);
                var json = await resp.Content.ReadAsStringAsync(ct);

                if (!resp.IsSuccessStatusCode)
                {
                    int code = (int)resp.StatusCode;

                    // 429 (rate limit) -> token saglam, sadece yavaslat ve ayni tokenle tekrar dene
                    if (code == 429)
                    {
                        if (rateRetries++ >= 3)
                        {
                            // Cok fazla 429 - vazgec, bu cagriyi atla
                            return null;
                        }
                        await Task.Delay(1200, ct);
                        continue; // ayni tokenle tekrar dene (PickActiveToken degismez)
                    }

                    // 402, 403 (kredi yok / hesap problemi) -> tokeni tukenmis say, sonrakine ge
                    if (code == 402 || code == 403)
                    {
                        MarkExhausted(token);
                        lastEx = new InvalidOperationException(
                            $"PlateRecognizer HTTP {code}: {Truncate(json, 150)}");
                        continue; // bir sonraki tokenle dene
                    }

                    // Diger hata - exception firlat
                    throw new InvalidOperationException(
                        $"PlateRecognizer HTTP {code}: {Truncate(json, 200)}");
                }

                // Basarili yanit
                var parsed = JsonConvert.DeserializeObject<PlateRecognizerResponse>(json);
                if (parsed?.results == null || parsed.results.Length == 0)
                {
                    AppLog($"API: plaka bulunamadi (results boş). Yanit: {Truncate(json, 150)}");
                    return null;
                }

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

            // Tum tokenler tukendi
            if (lastEx != null) throw lastEx;
            return null;
        }

        private static string Truncate(string s, int max)
            => s == null ? "" : s.Length <= max ? s.Trim() : s.Substring(0, max).Trim();

        private sealed class PlateRecognizerResponse { public PlateRecognizerResult[]? results { get; set; } }
        private sealed class PlateRecognizerResult { public PlateCandidate[]? candidates { get; set; } }
        private sealed class PlateCandidate { public string? plate { get; set; } public double score { get; set; } }

        private static DateTime _lastApiLog = DateTime.MinValue;
        private static void AppLog(string msg)
        {
            // 5 saniyede bir (spam onleme)
            if ((DateTime.Now - _lastApiLog).TotalSeconds < 5) return;
            _lastApiLog = DateTime.Now;
            try
            {
                File.AppendAllText(@"C:\Otopark\log.txt",
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} | {msg}{System.Environment.NewLine}");
            }
            catch { }
        }
    }
}
