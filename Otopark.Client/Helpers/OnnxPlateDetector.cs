using OpenCvSharp;
using System;
using System.Collections.Generic;
using OcvRect = OpenCvSharp.Rect;

namespace Otopark.Client.Helpers
{
    /// <summary>
    /// ONNX (YOLOv8) tabanli plaka detektoru DEVRE DISI.
    /// Microsoft.ML.OnnxRuntime nuget'i Windows Server 2012 R2 destek vermedigi icin
    /// kaldirildi. Lokal motor sadece Haar cascade + Tesseract ile calisir.
    /// Ileride ihtiyac olursa nuget'i geri ekleyip bu sinifi gercek implementasyona cevirin.
    /// </summary>
    internal sealed class OnnxPlateDetector : IDisposable
    {
        public bool IsAvailable => false;

        public List<OcvRect> Detect(Mat image) => new();

        public void Dispose() { }
    }
}
