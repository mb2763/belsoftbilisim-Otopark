using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OcvRect = OpenCvSharp.Rect;

namespace Otopark.Client.Helpers
{
    /// <summary>
    /// YOLOv8 ONNX modeli ile plaka bolgesi tespiti.
    /// Beklenen format:
    ///   Input:  [1, 3, 640, 640]  NCHW, RGB, normalized [0,1]
    ///   Output: [1, 5, 8400] (tek sinif: cx, cy, w, h, conf)  veya
    ///           [1, 84, 8400] (COCO: cx, cy, w, h + 80 sinif - sinif 0 secilir)
    /// Model dosyasi: C:\Otopark\models\plate_detector.onnx
    /// </summary>
    internal sealed class OnnxPlateDetector : IDisposable
    {
        public static readonly string ModelPath = @"C:\Otopark\models\plate_detector.onnx";
        private const int InputSize = 640;
        // ConfThreshold yukseltildi (0.55 -> 0.65): zemin/tabela hayalet detection'larini azalt.
        // Gercek plakalar genelde 0.85+ confidence ile gelir, bu kadar yukseltmek kaybi etkilemez.
        private const float ConfThreshold = 0.65f;
        private const float NmsThreshold = 0.45f;

        private InferenceSession? _session;
        private string _inputName = "images";
        private bool _disposed;

        public bool IsAvailable => _session != null;

        public OnnxPlateDetector()
        {
            try
            {
                if (!File.Exists(ModelPath))
                {
                    AppLog($"ONNX plaka detektoru yok: {ModelPath} (Otopark.Client.Helpers.PlateModelDownloader otomatik indirebilir)");
                    return;
                }

                var opts = new SessionOptions
                {
                    GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                    InterOpNumThreads = 1,
                    IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount / 2),
                };

                _session = new InferenceSession(ModelPath, opts);
                _inputName = _session.InputMetadata.Keys.FirstOrDefault() ?? "images";
                AppLog($"ONNX plaka detektoru hazir. Input: {_inputName}");
            }
            catch (Exception ex)
            {
                AppLog($"ONNX detector yuklenemedi: {ex.Message}");
                _session = null;
            }
        }

        public List<OcvRect> Detect(Mat image)
        {
            if (_session == null || image.Empty()) return new();

            try
            {
                int origW = image.Cols;
                int origH = image.Rows;

                var (resized, scale, padX, padY) = LetterboxResize(image, InputSize, InputSize);
                using (resized)
                {
                    var input = MatToTensorFast(resized);
                    var inputs = new[] { NamedOnnxValue.CreateFromTensor(_inputName, input) };
                    using var results = _session.Run(inputs);
                    var outTensor = results.First().AsTensor<float>();

                    var detections = ParseYoloV8Output(outTensor, scale, padX, padY, origW, origH);
                    return ApplyNms(detections, NmsThreshold);
                }
            }
            catch (Exception ex)
            {
                AppLog($"ONNX detect hata: {ex.Message}");
                return new();
            }
        }

        // ===== HELPERS =====

        private static (Mat resized, float scale, int padX, int padY) LetterboxResize(Mat src, int targetW, int targetH)
        {
            float scale = Math.Min((float)targetW / src.Cols, (float)targetH / src.Rows);
            int newW = (int)(src.Cols * scale);
            int newH = (int)(src.Rows * scale);
            int padX = (targetW - newW) / 2;
            int padY = (targetH - newH) / 2;

            using var scaled = new Mat();
            Cv2.Resize(src, scaled, new Size(newW, newH), interpolation: InterpolationFlags.Linear);

            var canvas = new Mat(targetH, targetW, MatType.CV_8UC3, new Scalar(114, 114, 114));
            scaled.CopyTo(new Mat(canvas, new OcvRect(padX, padY, newW, newH)));
            return (canvas, scale, padX, padY);
        }

        /// <summary>
        /// Mat'i ONNX tensor'una hizli kopyalar - per-pixel indexer yerine raw bytes kullanir.
        /// </summary>
        private static DenseTensor<float> MatToTensorFast(Mat mat)
        {
            using var rgb = new Mat();
            Cv2.CvtColor(mat, rgb, ColorConversionCodes.BGR2RGB);

            int rows = rgb.Rows;
            int cols = rgb.Cols;
            int channels = 3;
            var tensor = new DenseTensor<float>(new[] { 1, channels, rows, cols });

            // Raw byte buffer'i al ve CHW + normalize ile doldur
            byte[] data = new byte[rows * cols * channels];
            System.Runtime.InteropServices.Marshal.Copy(rgb.Data, data, 0, data.Length);

            var span = tensor.Buffer.Span;
            int planeSize = rows * cols;
            for (int y = 0; y < rows; y++)
            {
                int rowOff = y * cols * channels;
                for (int x = 0; x < cols; x++)
                {
                    int p = rowOff + x * channels;
                    span[0 * planeSize + y * cols + x] = data[p] / 255f;     // R
                    span[1 * planeSize + y * cols + x] = data[p + 1] / 255f; // G
                    span[2 * planeSize + y * cols + x] = data[p + 2] / 255f; // B
                }
            }
            return tensor;
        }

        private static List<Detection> ParseYoloV8Output(Tensor<float> output, float scale, int padX, int padY, int origW, int origH)
        {
            var dims = output.Dimensions;
            if (dims.Length != 3) return new();

            int channels = dims[1];
            int numBoxes = dims[2];
            int classCount = channels - 4;
            if (classCount < 1) return new();

            var detections = new List<Detection>();
            for (int i = 0; i < numBoxes; i++)
            {
                float cx = output[0, 0, i];
                float cy = output[0, 1, i];
                float w = output[0, 2, i];
                float h = output[0, 3, i];

                float maxScore = 0f;
                for (int c = 0; c < classCount; c++)
                {
                    float s = output[0, 4 + c, i];
                    if (s > maxScore) maxScore = s;
                }

                if (maxScore < ConfThreshold) continue;

                float x1 = (cx - w / 2 - padX) / scale;
                float y1 = (cy - h / 2 - padY) / scale;
                float x2 = (cx + w / 2 - padX) / scale;
                float y2 = (cy + h / 2 - padY) / scale;

                int ix1 = Math.Max(0, (int)x1);
                int iy1 = Math.Max(0, (int)y1);
                int ix2 = Math.Min(origW, (int)x2);
                int iy2 = Math.Min(origH, (int)y2);

                int bw = ix2 - ix1;
                int bh = iy2 - iy1;
                if (bw < 5 || bh < 5) continue;

                detections.Add(new Detection(new OcvRect(ix1, iy1, bw, bh), maxScore));
            }
            return detections;
        }

        private static List<OcvRect> ApplyNms(List<Detection> dets, float iouThreshold)
        {
            var sorted = dets.OrderByDescending(d => d.Confidence).ToList();
            var picked = new List<Detection>();
            while (sorted.Count > 0)
            {
                var top = sorted[0];
                picked.Add(top);
                sorted.RemoveAt(0);
                sorted.RemoveAll(d => Iou(top.Box, d.Box) > iouThreshold);
            }
            return picked.Select(d => d.Box).ToList();
        }

        private static float Iou(OcvRect a, OcvRect b)
        {
            int x1 = Math.Max(a.X, b.X);
            int y1 = Math.Max(a.Y, b.Y);
            int x2 = Math.Min(a.X + a.Width, b.X + b.Width);
            int y2 = Math.Min(a.Y + a.Height, b.Y + b.Height);
            int interW = Math.Max(0, x2 - x1);
            int interH = Math.Max(0, y2 - y1);
            int inter = interW * interH;
            int union = a.Width * a.Height + b.Width * b.Height - inter;
            return union <= 0 ? 0f : (float)inter / union;
        }

        private record Detection(OcvRect Box, float Confidence);

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
