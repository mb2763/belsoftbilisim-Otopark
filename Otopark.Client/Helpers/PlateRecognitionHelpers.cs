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

        /// <summary>
        /// EN NET KARE: bu plakayi olusturan okumalar icinde EN YUKSEK SKORLU karenin dosya yolu.
        /// Fotograf olarak bu kare kaydedilir; boylece DB'ye "OCR'in ilk takildigi" kare degil,
        /// plakanin en iyi okundugu kare gider. Tek-hit yolunda tetikleyen karenin kendisidir.
        /// null olabilir (yol verilmediyse) -> cagiran taraf tetikleyen kareye geri doner.
        /// </summary>
        public string? BestFramePath { get; }

        /// <summary>En net karenin skoru (olcum/log icin; BestFramePath null ise Score ile ayni).</summary>
        public double BestFrameScore { get; }

        public StablePlate(string plate, double score, string? bestFramePath = null, double bestFrameScore = 0)
        {
            Plate = plate;
            Score = score;
            BestFramePath = bestFramePath;
            BestFrameScore = bestFrameScore > 0 ? bestFrameScore : score;
        }
    }

    internal sealed class PlateStabilizer
    {
        private readonly double _minScore;
        private readonly double _windowSeconds;
        private readonly int _neededHits;
        // Path: o okumanin yapildigi TAM KARE dosyasi (ROI/temp DEGIL - bkz. TryDetectAndSetAsync).
        private readonly List<(string Plate, double Score, DateTime Ts, string? Path)> _buffer = new();

        // Push iki ayri yoldan cagrilabiliyor (FileSystemWatcher ve DetectFromFolder timer'i).
        // _buffer duz List oldugu icin es zamanli erisim listeyi bozar / LINQ'te
        // InvalidOperationException atar. Artik tum govde bu kilit altinda.
        private readonly object _lock = new();

        public PlateStabilizer(double minScore, double windowSeconds, int neededHits)
        { _minScore = minScore; _windowSeconds = windowSeconds; _neededHits = neededHits; }

        /// <param name="framePath">
        /// Bu okumanin yapildigi TAM KARE dosya yolu. ROI kirpmasindan gelen gecici dosya
        /// ASLA verilmemelidir; aksi halde fotograf olarak kirpilmis goruntu kaydedilir.
        /// (Yine de bir guvenlik agi olarak %TEMP%\OtoparkRoi altindaki yollar burada elenir.)
        /// </param>
        public StablePlate? Push(string plate, double score, DateTime utcNow, string? framePath = null)
        {
            if (score < _minScore) return null;

            // GUVENLIK AGI: ROI/temp yolu yanlislikla verilirse fotograf kirpik kaydedilmesin.
            // Yolu dusurmek zarasiz: o okuma yine sayilir, sadece "en net kare" adayi olmaz.
            if (!string.IsNullOrWhiteSpace(framePath))
            {
                try
                {
                    var roiKok = Path.Combine(Path.GetTempPath(), "OtoparkRoi");
                    if (Path.GetFullPath(framePath).StartsWith(roiKok, StringComparison.OrdinalIgnoreCase))
                        framePath = null;
                }
                catch { framePath = null; }   // yol cozulemiyorsa aday sayma
            }

            lock (_lock)
            {
            _buffer.Add((plate, score, utcNow, framePath));
            var cutoff = utcNow.AddSeconds(-_windowSeconds);
            _buffer.RemoveAll(x => x.Ts < cutoff);

            // Yuksek guvenli sonuc (>= 0.90) -> tek hit'te kabul.
            // Hareket halindeki araclar icin kritik: sadece 1-2 frame'de net plaka olabilir.
            //
            // DIKKAT: Bu kisayolun guvenligi TAMAMEN skoru kimin 0.90'a cikarabildigine bagli.
            // LocalPlateRecognizer eskiden skoru KOSULSUZ 0.90'a zorluyordu (Math.Max(0.90, ...)),
            // bu yuzden asagidaki 2-hit dogrulamasi fiilen olu kalmis ve bos koridor karesinde
            // uydurulan plakalar tek karede kabul edilmisti. Artik 0.90'i yalnizca ONNX
            // dedektorunden gelen VE en az 2 adayin uzlastigi okumalar gecebiliyor.
            if (score >= 0.90)
            {
                _buffer.Clear();
                // Tek kare var -> en net kare zaten tetikleyen karedir (davranis degismez).
                return new StablePlate(plate, score, framePath, score);
            }

            // Bu plakaya yakin (Levenshtein <= 2) tum okumalari ayni "grup" say.
            var matches = _buffer
                .Where(x => x.Score >= _minScore && Levenshtein(x.Plate, plate) <= 2)
                .ToList();

            int required = Math.Max(2, _neededHits);
            if (matches.Count < required) return null;

            // Aday secim onceligi:
            // 1) En uzun plaka tercih edilir (eksik karakter atlamadan tam okuma).
            // 2) Es uzunlukta en sik gorulen (consensus).
            // 3) Es sayida ise en yuksek skorlu okuma.
            var canonical = matches
                .GroupBy(x => x.Plate)
                .OrderByDescending(g => g.Key.Length)
                .ThenByDescending(g => g.Count())
                .ThenByDescending(g => g.Max(x => x.Score))
                .First().Key;

            var avg = matches.Average(x => x.Score);

            // EN NET KARE SECIMI:
            // Ayni plaka grubunun (Levenshtein<=2) kareleri arasindan EN YUKSEK SKORLU olani sec.
            // Once tam olarak canonical plakayi okuyan kareler denenir; yoksa tum gruba bakilir.
            // Tum adaylar AYNI plaka grubundan geldigi icin "yanlis aracin karesi" secilemez.
            var frameAdaylari = matches.Where(x => !string.IsNullOrEmpty(x.Path)).ToList();
            var canonicalAdaylari = frameAdaylari.Where(x => x.Plate == canonical).ToList();
            var enNet = (canonicalAdaylari.Count > 0 ? canonicalAdaylari : frameAdaylari)
                .OrderByDescending(x => x.Score)
                .Select(x => (Path: x.Path, Score: x.Score))
                .FirstOrDefault();

            _buffer.Clear();
            return new StablePlate(canonical, avg, enNet.Path, enNet.Score);
            }   // lock
        }

        /// <summary>
        /// Iki string arasinda Levenshtein (edit) mesafesi - max optimize.
        /// </summary>
        public static int Levenshtein(string a, string b)
        {
            if (string.IsNullOrEmpty(a)) return b?.Length ?? 0;
            if (string.IsNullOrEmpty(b)) return a.Length;
            int[,] d = new int[a.Length + 1, b.Length + 1];
            for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
            for (int j = 0; j <= b.Length; j++) d[0, j] = j;
            for (int i = 1; i <= a.Length; i++)
                for (int j = 1; j <= b.Length; j++)
                {
                    int cost = (a[i - 1] == b[j - 1]) ? 0 : 1;
                    d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                }
            return d[a.Length, b.Length];
        }
    }

    internal sealed class DuplicateSuppressor
    {
        private readonly double _suppressSeconds;
        private readonly Dictionary<string, DateTime> _lastSent = new();

        public DuplicateSuppressor(double suppressSeconds) { _suppressSeconds = suppressSeconds; }

        public bool ShouldSuppress(string plate, DateTime utcNow)
        {
            // FIX 2 — Plaka normalize (büyük harf + bosluk yok) ki "38 JS 837"
            // ile "38JS837" ayni sayilsin.
            string norm = Normalize(plate);

            // Suresi gecmemis kayitlardan herhangi biriyle:
            //  - Ayni il kodu (ilk 2 hane) ZORUNLU — yoksa farkli arac
            //  - Sonra Levenshtein <= 2 ise benzer plaka say.
            // Bu, "38JS837" girisi sonrasi "38JS83" gibi yakin OCR'lari ayni arac
            // sayar; "06JS837" gibi farkli il kodlulari ayri tutar.
            foreach (var kv in _lastSent)
            {
                if ((utcNow - kv.Value).TotalSeconds >= _suppressSeconds) continue;
                string kn = kv.Key;
                if (kn == norm) return true;
                // Il kodu (ilk 2 karakter) eslesmeli
                if (kn.Length >= 2 && norm.Length >= 2 && kn.Substring(0, 2) != norm.Substring(0, 2))
                    continue;
                if (PlateStabilizer.Levenshtein(kn, norm) <= 2)
                    return true;
            }

            _lastSent[norm] = utcNow;

            var oldCutoff = utcNow.AddMinutes(-10);
            foreach (var k in _lastSent.Where(kv => kv.Value < oldCutoff).Select(kv => kv.Key).ToList())
                _lastSent.Remove(k);

            return false;
        }

        private static string Normalize(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var arr = s.ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray();
            return new string(arr);
        }
    }

    internal static class PlateRules
    {
        // Sehir kodu (01-81) + harf + rakam.
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

            // 70+ ulkenin format kutuphanesi - PDF'ten cikarilmis 660 plaka pattern'i.
            // Eslesme varsa direk kabul (en guclu sinyal).
            if (Otopark.Client.Helpers.Plate.PlateFormatLibrary.IsKnownFormat(plate))
                return true;

            // TR plaka formati eslesirse direk kabul (yedek).
            if (TrPlate.IsMatch(plate)) return true;

            // YABANCI PLAKA TOLERANSI:
            // Eskiden "en az 2 harf VE en az 2 rakam" sarti vardi. Bu sart Belcika/Hollanda
            // kisisellestirilmis plakalarini (orn. "AKKUS-H" -> AKKUSH, 0 rakam) reddediyordu.
            // 1388 gercek kare uzerinde olculdu: ONNX dedektoru SADECE gercek plakalarda kutu
            // uretiyor (bos koridorda 0 kutu). Yani asil kapi dedektor, metin kurali degil.
            // Bu yuzden sart gevsetildi: en az 1 harf yeterli.
            // "En az 1 harf" saf-rakam cop okumalarini (orn. "208188") hala eliyor.
            // Tabela kelimeleri asagidaki blacklist + Levenshtein kontrolleriyle korunuyor.
            int letterCount = plate.Count(char.IsLetter);
            if (letterCount < 1) return false;

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

            // Levenshtein mesafe kontrolu (5+ karakterli sign kelimeleri icin):
            // OCR yanilirsa "C" yerine "S", "G" yerine "C" gibi 1 harf hata yapabilir.
            // "S1K1S" -> "SIKIS" -> CIKIS'a 1 mesafede -> reddedilir.
            // "GBRIS" -> GIRIS'a 1 mesafede -> reddedilir.
            if (letterizedLetters.Length >= 5)
            {
                foreach (var word in BlacklistWords)
                {
                    if (word.Length >= 5 && LevenshteinDistance(letterizedLetters, word) <= 1)
                        return false;
                    // Tum kelimeyle de kontrol et (rakam + harf karisik halde)
                    if (word.Length >= 5 && LevenshteinDistance(letterized, word) <= 1)
                        return false;
                }
            }

            // Tek karakterin tekrari (AAAAAA, 11111A gibi) - geçersiz
            if (plate.Distinct().Count() < 3) return false;

            return true;
        }

        /// <summary>
        /// PERSONELIN ELLE YAZDIGI plaka icin gevsek dogrulama.
        /// Otomatik okuma kurallari (IsLikelyPlate) burada UYGULANMAZ: personel plakayi
        /// gozuyle gormustur, bizim format kutuphanemizden daha guvenilirdir.
        /// Ozellikle yabanci/kisisellestirilmis plakalar (AKKUS-H, NO-MF-38) elle
        /// yazilabilsin diye harf/rakam sayisi sarti yoktur.
        /// Sadece bariz sacmaliklari eler.
        /// </summary>
        public static bool IsAcceptableManualPlate(string plate)
        {
            if (string.IsNullOrWhiteSpace(plate)) return false;
            if (plate.Length < 4 || plate.Length > 12) return false;
            if (!plate.All(char.IsLetterOrDigit)) return false;
            if (plate.Distinct().Count() < 2) return false;
            // Personel yanlislikla tabela kelimesi yazarsa yine de engelle
            if (BlacklistWords.Contains(plate)) return false;
            return true;
        }

        /// <summary>
        /// İki string arasindaki Levenshtein (edit) mesafesini hesaplar.
        /// "CIKIS" - "SIKIS" -> 1 (sadece ilk harf farkli)
        /// </summary>
        private static int LevenshteinDistance(string a, string b)
        {
            if (string.IsNullOrEmpty(a)) return b?.Length ?? 0;
            if (string.IsNullOrEmpty(b)) return a.Length;

            int[,] d = new int[a.Length + 1, b.Length + 1];
            for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
            for (int j = 0; j <= b.Length; j++) d[0, j] = j;

            for (int i = 1; i <= a.Length; i++)
            {
                for (int j = 1; j <= b.Length; j++)
                {
                    int cost = (a[i - 1] == b[j - 1]) ? 0 : 1;
                    d[i, j] = Math.Min(
                        Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                        d[i - 1, j - 1] + cost);
                }
            }
            return d[a.Length, b.Length];
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

        /// <summary>
        /// Kullanilabilir (tukenmemis / gecersiz isaretlenmemis) token var mi?
        /// Yoksa buluta hic gidilmez — bkz. RecognizeAsync basindaki erken cikis.
        /// </summary>
        public bool KullanilabilirTokenVar
        {
            get
            {
                var cutoff = DateTime.UtcNow.AddHours(-1);
                foreach (var t in _tokens)
                    if (!_tokenExhaustedAt.TryGetValue(t, out var e) || e <= cutoff)
                        return true;
                return false;
            }
        }

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

            // ERKEN CIKIS: hicbir token kullanilabilir degilse buluta HIC gitme.
            // Onceden once asagidaki hiz sinirlayicida ~1.1 sn BEKLENIYOR, sonra
            // "token yok" deyip cikiliyordu. Token gecersiz oldugunda bu, HER KARE icin
            // bosa 1 saniye demekti; islenebilen kare sayisi dusuyor ve yerel motorun
            // plakayi yakalama sansi azaliyordu.
            if (!KullanilabilirTokenVar) return null;

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

                // FIX 4 — Yerel ön-işleme:
                //   - 1 MB üstü dosyalari OpenCV ile küçült (uzun kenar 1600px, JPEG q=85)
                //   - CLAHE (Contrast Limited Adaptive Histogram Equalization) ile kontrast iyilestir
                //   - Boyut > 30 KB ise; çok küçük olanlara dokunma (deformasyon riski)
                bytes = PreprocessForApi(bytes);

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
                        // Format kutuphanesi eslesmesi ARTIK ZORUNLU DEGIL: kutuphanede
                        // olmayan yabanci/kisisellestirilmis plakalar da kabul edilmeli.
                        // Tabela/cop okumalari IsLikelyPlate ile eleniyor.
                        if (!PlateRules.IsLikelyPlate(normalized)) continue;
                        if (best == null || c.score > best.Value.score)
                            best = (normalized, c.score);
                    }
                }

                if (best == null) return null;

                return new PlateRecognitionResult(best.Value.plate, best.Value.score);
            }

            // Tum tokenler tukendi
            if (lastEx != null) throw lastEx;
            return null;
        }

        private static string Truncate(string s, int max)
            => s == null ? "" : s.Length <= max ? s.Trim() : s.Substring(0, max).Trim();

        /// <summary>
        /// FIX 4 — Yerel on-isleme. API'ye gondermeden once goruntuyu optimize eder:
        ///   - 1 MB ustu dosyalari uzun kenar 1600px JPEG q=85'e indirir (upload hizlanir).
        ///   - Plate Recognizer 5MB ust limiti var; bunu da garantiler.
        ///   - Cok kucuk goruntulere dokunmaz (deformasyon riski).
        /// OpenCV native baslayamamissa orijinal byte'lari geri verir.
        /// </summary>
        private static byte[] PreprocessForApi(byte[] original)
        {
            try
            {
                if (original == null || original.Length < 32 * 1024)
                    return original ?? Array.Empty<byte>();

                // Sadece 1 MB ustu dosyalari islemeye al
                if (original.Length < 1 * 1024 * 1024)
                    return original;

                using var src = OpenCvSharp.Cv2.ImDecode(original, OpenCvSharp.ImreadModes.Color);
                if (src == null || src.Empty()) return original;

                int longEdge = Math.Max(src.Width, src.Height);
                const int target = 1600;
                OpenCvSharp.Mat scaled;
                if (longEdge > target)
                {
                    double scale = (double)target / longEdge;
                    int nw = (int)(src.Width * scale);
                    int nh = (int)(src.Height * scale);
                    scaled = new OpenCvSharp.Mat();
                    OpenCvSharp.Cv2.Resize(src, scaled, new OpenCvSharp.Size(nw, nh),
                        interpolation: OpenCvSharp.InterpolationFlags.Area);
                }
                else
                {
                    scaled = src.Clone();
                }

                using (scaled)
                {
                    var encParams = new int[] { (int)OpenCvSharp.ImwriteFlags.JpegQuality, 85 };
                    OpenCvSharp.Cv2.ImEncode(".jpg", scaled, out byte[] encoded, encParams);
                    if (encoded != null && encoded.Length > 0 && encoded.Length < original.Length)
                        return encoded;
                    return original;
                }
            }
            catch
            {
                return original;
            }
        }

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
