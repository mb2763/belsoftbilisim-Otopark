using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Otopark.Client.Helpers
{
    /// <summary>
    /// ONNX OCR tek okuma sonucu.
    /// </summary>
    internal readonly record struct PlateOcrResult(
        string Plate,
        double Score,
        double MinCharProb,
        int RegionId,
        double RegionProb)
    {
        public static readonly PlateOcrResult Bos = new("", 0, 0, -1, 0);

        /// <summary>fast-plate-ocr global modelinde gozlemlenen TR bolge sinifi.</summary>
        public const int TrRegionId = 60;

        public bool TrBolgesi => RegionId == TrRegionId;
    }

    /// <summary>
    /// FastPlateOCR-uyumlu ONNX modeli ile plaka karakter tanima.
    /// Apache 2.0 acik kaynak modellerle calisir (ankandrew/fast-plate-ocr).
    ///
    /// Beklenen input: [1, H, W, C] veya [1, C, H, W] grayscale veya RGB
    /// Default ayar: 64x224 grayscale [1, 64, 224, 1]
    /// Output: [1, MaxPlateLen, AlphabetSize] - logits
    /// Argmax + alphabet lookup ile karakter dizisine cevrilir.
    ///
    /// Model dosyasi: C:\Otopark\models\plate_ocr.onnx
    /// </summary>
    internal sealed class OnnxPlateOcr : IDisposable
    {
        public static readonly string ModelPath = @"C:\Otopark\models\plate_ocr.onnx";

        // FastPlateOCR cct_s_v2_global config (RGB 64x128).
        // Padding karakteri "_" alphabet'in son elemani.
        private const int InputH = 64;   // img_height
        private const int InputW = 128;  // img_width
        private const int InputChannels = 3; // RGB (eskiden 1=grayscale idi)
        private const string Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ_";
        private const int MaxPlateLength = 9;

        private InferenceSession? _session;
        private string _inputName = "input";
        private bool _channelsLast = true; // FastPlateOCR genelde NHWC
        private bool _disposed;

        public bool IsAvailable => _session != null;

        public OnnxPlateOcr()
        {
            try
            {
                if (!File.Exists(ModelPath))
                {
                    AppLog($"ONNX OCR modeli yok: {ModelPath}");
                    return;
                }

                var opts = new SessionOptions
                {
                    GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                    InterOpNumThreads = 1,
                    IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount / 2),
                };

                _session = new InferenceSession(ModelPath, opts);
                var inputMeta = _session.InputMetadata.First();
                _inputName = inputMeta.Key;

                // Input shape'ten layout cikar
                var dims = inputMeta.Value.Dimensions;
                if (dims.Length == 4)
                {
                    // NHWC: [N, H, W, C] - C kucuk (1 veya 3)
                    // NCHW: [N, C, H, W] - C kucuk
                    _channelsLast = dims[3] <= 4 && dims[1] > 4;
                }
                AppLog($"ONNX OCR hazir. Input: {_inputName} [{string.Join(",", dims)}] layout={(_channelsLast ? "NHWC" : "NCHW")}");
            }
            catch (Exception ex)
            {
                AppLog($"ONNX OCR yuklenemedi: {ex.Message}");
                _session = null;
            }
        }

        /// <summary>
        /// Verilen plaka bolgesinden karakter dizisi okur. Bos string = okuyamadi.
        /// Score        : karakter guvenlerinin ORTALAMASI [0,1]
        /// MinCharProb  : EN ZAYIF karakterin guveni [0,1]  <-- guven kapisinda bu kullanilir
        /// RegionId     : modelin ikinci ciktisi (ulke/bolge sinifi), -1 = okunamadi
        /// </summary>
        public PlateOcrResult Recognize(Mat plateRegion)
        {
            if (_session == null || plateRegion.Empty()) return PlateOcrResult.Bos;

            try
            {
                // 1) Model'in bekledigi format icin hazirla:
                //    - 1 kanal (grayscale): BGR -> GRAY
                //    - 3 kanal (RGB): BGR -> RGB
                using var prepared = new Mat();
                if (InputChannels == 1)
                {
                    if (plateRegion.Channels() > 1)
                        Cv2.CvtColor(plateRegion, prepared, ColorConversionCodes.BGR2GRAY);
                    else
                        plateRegion.CopyTo(prepared);
                }
                else
                {
                    // RGB modelleri (cct_s_v2_global gibi) BGR'yi RGB'ye cevir
                    if (plateRegion.Channels() == 1)
                        Cv2.CvtColor(plateRegion, prepared, ColorConversionCodes.GRAY2RGB);
                    else
                        Cv2.CvtColor(plateRegion, prepared, ColorConversionCodes.BGR2RGB);
                }

                using var resized = new Mat();
                Cv2.Resize(prepared, resized, new Size(InputW, InputH), interpolation: InterpolationFlags.Linear);

                // 2) UInt8 tensor olusturulup model'e gonderilir
                var tensor = MatToByteTensor(resized);

                var inputs = new[] { NamedOnnxValue.CreateFromTensor(_inputName, tensor) };
                using var results = _session.Run(inputs);

                // Model IKI cikti veriyor:
                //   "plate"  [N, 10, 37] -> karakter olasiliklari
                //   "region" [N, 66]     -> ulke/bolge sinifi (FAZ 3'te kullanilir)
                Tensor<float>? plateOut = null, regionOut = null;
                foreach (var r in results)
                {
                    var t = r.AsTensor<float>();
                    if (t.Dimensions.Length == 3) plateOut ??= t;        // [N,T,C]
                    else if (t.Dimensions.Length == 2 && t.Dimensions[1] > 40) regionOut ??= t;
                    else plateOut ??= t;                                  // [N,T*C] duzlestirilmis
                }
                if (plateOut == null) return PlateOcrResult.Bos;

                var (plate, score, minProb) = DecodeGreedy(plateOut);
                int regionId = -1;
                double regionProb = 0;
                if (regionOut != null && regionOut.Dimensions.Length == 2)
                {
                    int n = regionOut.Dimensions[1];
                    float en = float.MinValue;
                    for (int i = 0; i < n; i++)
                    {
                        float v = regionOut[0, i];
                        if (v > en) { en = v; regionId = i; }
                    }
                    regionProb = en;
                }

                return new PlateOcrResult(plate, score, minProb, regionId, regionProb);
            }
            catch (Exception ex)
            {
                AppLog($"ONNX OCR inference hata: {ex.Message}");
                return PlateOcrResult.Bos;
            }
        }

        // ===== HELPERS =====

        private DenseTensor<byte> MatToByteTensor(Mat mat)
        {
            int h = mat.Rows;
            int w = mat.Cols;
            int c = InputChannels;

            int[] shape = _channelsLast ? new[] { 1, h, w, c } : new[] { 1, c, h, w };
            var tensor = new DenseTensor<byte>(shape);
            var span = tensor.Buffer.Span;

            byte[] data = new byte[h * w * c];
            System.Runtime.InteropServices.Marshal.Copy(mat.Data, data, 0, data.Length);

            if (_channelsLast)
            {
                // NHWC: data zaten interleaved (HWC) -> dogrudan kopyala
                for (int i = 0; i < data.Length; i++)
                    span[i] = data[i];
            }
            else
            {
                // NCHW: HWC -> CHW transpose
                int planeSize = h * w;
                for (int y = 0; y < h; y++)
                {
                    int rowOff = y * w * c;
                    for (int x = 0; x < w; x++)
                    {
                        int p = rowOff + x * c;
                        for (int ch = 0; ch < c; ch++)
                            span[ch * planeSize + y * w + x] = data[p + ch];
                    }
                }
            }
            return tensor;
        }

        /// <summary>
        /// Greedy cozumleme.
        ///
        /// ONEMLI DUZELTME (06.08.2026) - CIFTE SOFTMAX:
        /// Bu dosyanin eski basligi "Output: logits" diyordu ve asagida ciktiya softmax
        /// uygulaniyordu. Kullandigimiz model (fast-plate-ocr cct_s_v2_global) ciktiyi
        /// ZATEN SOFTMAX'LANMIS veriyor - olculdu: her zaman dilimi satirinin toplami 1.000.
        /// Uzerine ikinci kez softmax uygulaninca model %100 eminken bile skor
        ///     exp(0) / (exp(0) + 36*exp(-1)) ~= 0.07
        /// cikiyordu. Tum guven zinciri (LocalPlateRecognizer'daki +0.55 takviye ve
        /// 0.90'a zorlama) bu bozuk olcumu telafi etmek icin kurulmustu.
        ///
        /// Ham olasiliklarla olculen ayrisma (51 arac gecisi):
        ///     46 DOGRU okumanin hepsi  : min karakter guveni >= 0.99
        ///      5 HATALI okumanin hepsi : 0.23 - 0.70
        /// Yani min-karakter guveni tek basina hatayi kusursuz ayirt ediyor.
        ///
        /// Model degisirse diye ciktinin olasilik mi logit mi oldugu OTOMATIK algilanir:
        /// satir toplami ~1.0 ise olasilik kabul edilir, degilse softmax uygulanir.
        /// </summary>
        private static (string Plate, double Score, double MinCharProb) DecodeGreedy(Tensor<float> output)
        {
            var dims = output.Dimensions;
            if (dims.Length < 2) return ("", 0, 0);

            int seqLen, numClasses;
            if (dims.Length == 3)
            {
                // [1, T, C]
                seqLen = dims[1];
                numClasses = dims[2];
            }
            else if (dims.Length == 2)
            {
                // [1, T*C] - duzlestirilmis
                seqLen = MaxPlateLength;
                numClasses = dims[1] / seqLen;
            }
            else return ("", 0, 0);

            if (numClasses == 0 || seqLen == 0) return ("", 0, 0);

            // dims bir 'ref local' oldugu icin lambda icinde kullanilamaz -> kopyala
            int rank = dims.Length;
            int sinifSayisi = numClasses;
            float Deger(int t, int c) => rank == 3 ? output[0, t, c] : output[0, t * sinifSayisi + c];

            // --- Cikti olasilik mi, logit mi? Ilk zaman diliminin toplamina bak. ---
            float ilkToplam = 0f;
            bool negatifVar = false;
            for (int c = 0; c < numClasses; c++)
            {
                float v = Deger(0, c);
                ilkToplam += v;
                if (v < -0.0001f) negatifVar = true;
            }
            bool olasilikCiktisi = !negatifVar && Math.Abs(ilkToplam - 1f) < 0.02f;

            var sb = new StringBuilder();
            double scoreSum = 0;
            double minProb = 1.0;
            int scoreCount = 0;

            for (int t = 0; t < seqLen; t++)
            {
                int bestIdx = 0;
                float prob;

                if (olasilikCiktisi)
                {
                    // Dogrudan olasilik: sadece argmax
                    float best = float.MinValue;
                    for (int c = 0; c < numClasses; c++)
                    {
                        float v = Deger(t, c);
                        if (v > best) { best = v; bestIdx = c; }
                    }
                    prob = Math.Clamp(best, 0f, 1f);
                }
                else
                {
                    // Logit: numerik stabil softmax
                    float maxLogit = float.MinValue;
                    for (int c = 0; c < numClasses; c++)
                    {
                        float v = Deger(t, c);
                        if (v > maxLogit) maxLogit = v;
                    }
                    float sumExp = 0f, bestExp = 0f;
                    for (int c = 0; c < numClasses; c++)
                    {
                        float e = MathF.Exp(Deger(t, c) - maxLogit);
                        sumExp += e;
                        if (e > bestExp) { bestExp = e; bestIdx = c; }
                    }
                    prob = sumExp > 0 ? bestExp / sumExp : 0f;
                }

                if (bestIdx >= 0 && bestIdx < Alphabet.Length)
                {
                    char ch = Alphabet[bestIdx];
                    if (ch != '_') // padding karakteri
                    {
                        sb.Append(ch);
                        scoreSum += prob;
                        if (prob < minProb) minProb = prob;
                        scoreCount++;
                    }
                }
            }

            string plate = sb.ToString();
            double avgScore = scoreCount > 0 ? scoreSum / scoreCount : 0;
            return (plate, avgScore, scoreCount > 0 ? minProb : 0);
        }

        private static void AppLog(string msg)
        {
            try
            {
                File.AppendAllText(@"C:\Otopark\log.txt",
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} | {msg}{Environment.NewLine}");
            }
            catch { }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _session?.Dispose();
                _disposed = true;
            }
        }
    }
}
