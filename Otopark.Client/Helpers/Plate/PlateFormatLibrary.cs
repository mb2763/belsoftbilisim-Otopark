using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;

namespace Otopark.Client.Helpers.Plate
{
    /// <summary>
    /// 70+ ulkenin ~660 plaka formatini icerir. plate-formats.txt embedded resource'dan
    /// yuklenir; her satir "XX|PATTERN" formatinda (XX=ulke kodu, A=harf, 9=rakam).
    /// Pattern -> Regex donusumu tek seferlik yapilir, runtime'da cache'lenir.
    /// </summary>
    public static class PlateFormatLibrary
    {
        public sealed class PlateFormat
        {
            public string CountryCode { get; }
            public string Pattern { get; }
            public Regex Regex { get; }
            public int Length { get; }

            public PlateFormat(string country, string pattern, Regex regex)
            {
                CountryCode = country;
                Pattern = pattern;
                Regex = regex;
                Length = pattern.Count(c => c == 'A' || c == '9');
            }

            public override string ToString() => $"({CountryCode}) {Pattern}";
        }

        private static readonly Lazy<List<PlateFormat>> _all = new(LoadAll);
        private static readonly Lazy<Dictionary<string, List<PlateFormat>>> _byCountry =
            new(() => _all.Value.GroupBy(f => f.CountryCode).ToDictionary(g => g.Key, g => g.ToList()));
        private static readonly Lazy<Dictionary<int, List<PlateFormat>>> _byLength =
            new(() => _all.Value.GroupBy(f => f.Length).ToDictionary(g => g.Key, g => g.ToList()));

        public static IReadOnlyList<PlateFormat> All => _all.Value;
        public static IReadOnlyDictionary<string, List<PlateFormat>> ByCountry => _byCountry.Value;

        /// <summary>
        /// Normalized plakanin (sadece A-Z + 0-9) bilinen herhangi bir ulke formatina uyup uymadigini doner.
        /// TR formati icin il kodu (01-81) zorunlu.
        /// </summary>
        public static bool IsKnownFormat(string normalizedPlate)
        {
            if (string.IsNullOrWhiteSpace(normalizedPlate)) return false;
            if (!_byLength.Value.TryGetValue(normalizedPlate.Length, out var candidates)) return false;
            foreach (var f in candidates)
            {
                if (!f.Regex.IsMatch(normalizedPlate)) continue;
                if (f.CountryCode == "TR" && !IsValidTrCityCode(normalizedPlate)) continue;
                return true;
            }
            return false;
        }

        /// <summary>
        /// Eslesen format'i doner. TR oncelikli - varsa TR formati ilk doner,
        /// yoksa eslesen ilk format. Eslesme yoksa null.
        /// TR formati icin EK kontrol: il kodu (ilk 2 rakam) 01-81 araliginda olmali.
        /// "98NE4484" gibi gecersiz il kodlu plakalar reddedilir.
        /// </summary>
        public static PlateFormat? Match(string normalizedPlate)
        {
            if (string.IsNullOrWhiteSpace(normalizedPlate)) return null;
            if (!_byLength.Value.TryGetValue(normalizedPlate.Length, out var candidates)) return null;
            PlateFormat? firstMatch = null;
            foreach (var f in candidates)
            {
                if (!f.Regex.IsMatch(normalizedPlate)) continue;
                if (f.CountryCode == "TR")
                {
                    // TR formati eslesti - il kodu (01-81) kontrolu zorunlu
                    if (!IsValidTrCityCode(normalizedPlate)) continue;
                    return f;
                }
                if (firstMatch == null) firstMatch = f;
            }
            return firstMatch;
        }

        /// <summary>
        /// Plakanin ilk 2 rakaminin gecerli Turk il kodu (01-81) olup olmadigini kontrol eder.
        /// </summary>
        private static bool IsValidTrCityCode(string plate)
        {
            if (plate == null || plate.Length < 2) return false;
            if (!char.IsDigit(plate[0]) || !char.IsDigit(plate[1])) return false;
            int code = (plate[0] - '0') * 10 + (plate[1] - '0');
            return code >= 1 && code <= 81;
        }

        /// <summary>
        /// Plakaya uyan TUM ulke formatlarini doner (TR'yi en basta).
        /// </summary>
        public static IEnumerable<PlateFormat> AllMatches(string normalizedPlate)
        {
            if (string.IsNullOrWhiteSpace(normalizedPlate)) yield break;
            if (!_byLength.Value.TryGetValue(normalizedPlate.Length, out var candidates)) yield break;
            // TR'yi once dondur
            foreach (var f in candidates)
                if (f.CountryCode == "TR" && f.Regex.IsMatch(normalizedPlate)) yield return f;
            foreach (var f in candidates)
                if (f.CountryCode != "TR" && f.Regex.IsMatch(normalizedPlate)) yield return f;
        }

        /// <summary>
        /// Ulke koduna gore eslesen format'i doner.
        /// </summary>
        public static PlateFormat? MatchForCountry(string normalizedPlate, string countryCode)
        {
            if (string.IsNullOrWhiteSpace(normalizedPlate) || string.IsNullOrWhiteSpace(countryCode)) return null;
            if (!_byCountry.Value.TryGetValue(countryCode.ToUpperInvariant(), out var list)) return null;
            foreach (var f in list)
                if (f.Regex.IsMatch(normalizedPlate)) return f;
            return null;
        }

        public static IEnumerable<PlateFormat> ForCountry(string countryCode)
        {
            if (string.IsNullOrWhiteSpace(countryCode)) return Array.Empty<PlateFormat>();
            return _byCountry.Value.TryGetValue(countryCode.ToUpperInvariant(), out var list)
                ? list : Array.Empty<PlateFormat>();
        }

        public static bool IsTurkishPlate(string normalizedPlate) =>
            MatchForCountry(normalizedPlate, "TR") != null;

        // ===== Yukleme =====

        private static List<PlateFormat> LoadAll()
        {
            var result = new List<PlateFormat>(700);
            try
            {
                var asm = Assembly.GetExecutingAssembly();
                var resName = asm.GetManifestResourceNames()
                    .FirstOrDefault(n => n.EndsWith("plate-formats.txt", StringComparison.OrdinalIgnoreCase));
                if (resName == null) return result;

                using var stream = asm.GetManifestResourceStream(resName);
                if (stream == null) return result;
                using var reader = new StreamReader(stream);

                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    line = line.Trim();
                    if (line.Length == 0 || !line.Contains('|')) continue;

                    var parts = line.Split('|', 2);
                    if (parts.Length != 2) continue;

                    var country = parts[0].Trim().ToUpperInvariant();
                    var pattern = parts[1].Trim().ToUpperInvariant();
                    if (country.Length != 2 || pattern.Length == 0) continue;

                    var regex = PatternToRegex(pattern);
                    if (regex == null) continue;

                    result.Add(new PlateFormat(country, pattern, regex));
                }
            }
            catch { /* yutuldu - format kutuphanesi yoksa sistem yine calisir */ }

            return result;
        }

        /// <summary>
        /// Pattern karakterlerini regex'e cevirir:
        ///   A -> [A-Z], 9 -> [0-9], digerleri (-, _) literal kabul edilmez (normalize edilir).
        /// Anchored (^...$) regex doner.
        /// </summary>
        private static Regex? PatternToRegex(string pattern)
        {
            var sb = new System.Text.StringBuilder("^");
            foreach (var c in pattern)
            {
                switch (c)
                {
                    case 'A': sb.Append("[A-Z]"); break;
                    case '9': sb.Append("[0-9]"); break;
                    case '-': case '_': case ' ':
                        // Normalized girdide ayrac olmaz, atla.
                        break;
                    default:
                        // Beklenmeyen karakter -> formatin kendisi gecersiz say
                        return null;
                }
            }
            sb.Append('$');
            try
            {
                return new Regex(sb.ToString(), RegexOptions.Compiled | RegexOptions.CultureInvariant);
            }
            catch { return null; }
        }
    }
}
