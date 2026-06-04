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
        /// Score: [0,1] arasi ortalama softmax confidence.
        /// </summary>
        public (string Plate, double Score) Recognize(Mat plateRegion)
        {
            if (_session == null || plateRegion.Empty()) return ("", 0);

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
                var output = results.First().AsTensor<float>();

                return DecodeGreedy(output);
            }
            catch (Exception ex)
            {
                AppLog($"ONNX OCR inference hata: {ex.Message}");
                return ("", 0);
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

        private static (string Plate, double Score) DecodeGreedy(Tensor<float> output)
        {
            var dims = output.Dimensions;
            if (dims.Length < 2) return ("", 0);

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
            else return ("", 0);

            if (numClasses == 0 || seqLen == 0) return ("", 0);

            var sb = new StringBuilder();
            double scoreSum = 0;
            int scoreCount = 0;

            for (int t = 0; t < seqLen; t++)
            {
                // Softmax + argmax (numerik stabil)
                float maxLogit = float.MinValue;
                for (int c = 0; c < numClasses; c++)
                {
                    float v = dims.Length == 3 ? output[0, t, c] : output[0, t * numClasses + c];
                    if (v > maxLogit) maxLogit = v;
                }

                float sumExp = 0f;
                int bestIdx = 0;
                float bestProb = 0f;
                for (int c = 0; c < numClasses; c++)
                {
                    float v = dims.Length == 3 ? output[0, t, c] : output[0, t * numClasses + c];
                    float e = MathF.Exp(v - maxLogit);
                    sumExp += e;
                    if (e > bestProb)
                    {
                        bestProb = e;
                        bestIdx = c;
                    }
                }
                float prob = sumExp > 0 ? bestProb / sumExp : 0f;

                if (bestIdx >= 0 && bestIdx < Alphabet.Length)
                {
                    char ch = Alphabet[bestIdx];
                    if (ch != '_') // padding karakteri
                    {
                        sb.Append(ch);
                        scoreSum += prob;
                        scoreCount++;
                    }
                }
            }

            string plate = sb.ToString();
            double avgScore = scoreCount > 0 ? scoreSum / scoreCount : 0;
            return (plate, avgScore);
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
