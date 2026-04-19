using System;
using System.Drawing;
using System.Drawing.Printing;
using System.IO;

namespace Otopark.Client.Services;

public static class ReceiptPrintService
{
    // Logo dosyalari klasoru - degistirmek icin bu satiri guncelleyin
    // private const string LogoFolder = @"C:\Otopark\";
    private const string LogoFolder = @"D:\GESI\OTOPARK\";

    private static Image? LoadLogo(string fileName)
    {
        try
        {
            var path = Path.Combine(LogoFolder, fileName);
            if (File.Exists(path))
                return Image.FromFile(path);
        }
        catch { }
        return null;
    }

    /// <summary>
    /// Giris fisi basar
    /// </summary>
    public static void PrintEntryReceipt(ReceiptData data)
    {
        var doc = new PrintDocument();
        doc.PrintPage += (sender, e) => DrawEntryReceipt(e.Graphics!, data);
        doc.Print();
    }

    /// <summary>
    /// Cikis fisi basar
    /// </summary>
    public static void PrintExitReceipt(ReceiptData data)
    {
        var doc = new PrintDocument();
        doc.PrintPage += (sender, e) => DrawExitReceipt(e.Graphics!, data);
        doc.Print();
    }

    private static void DrawEntryReceipt(Graphics g, ReceiptData data)
    {
        var fontTitle = new Font("Arial", 10, FontStyle.Bold);
        var fontSubtitle = new Font("Arial", 8, FontStyle.Regular);
        var fontContent = new Font("Arial", 8, FontStyle.Regular);
        var fontBold = new Font("Arial", 8, FontStyle.Bold);

        float y = 10;
        float x = 10;
        float lineHeight = 16;

        // Logolar
        using var imarLogo = LoadLogo("IMAR LOGO.bmp");
        using var maliyeLogo = LoadLogo("MALIYE LOGO.bmp");
        if (imarLogo != null)
            g.DrawImage(imarLogo, 90, y, 80, 80);
        y += imarLogo != null ? 85 : 0;

        // Baslik
        g.DrawString("Kayseri Ulasim A.S.", fontTitle, Brushes.Black, x, y);
        y += lineHeight + 4;
        g.DrawString("Kapali Otopark", fontSubtitle, Brushes.Black, x, y);
        y += lineHeight;
        g.DrawString("Perakende Satis Fisi", fontSubtitle, Brushes.Black, x, y);
        y += lineHeight + 8;

        // Cizgi
        g.DrawString("--------------------------------", fontContent, Brushes.Black, x, y);
        y += lineHeight;

        // Bilgiler
        g.DrawString($"Fis No     : {data.ReceiptNo}", fontContent, Brushes.Black, x, y);
        y += lineHeight;
        g.DrawString($"Tarih      : {data.DateTime:dd.MM.yyyy HH:mm}", fontContent, Brushes.Black, x, y);
        y += lineHeight;
        g.DrawString($"Plaka      : {data.Plate}", fontBold, Brushes.Black, x, y);
        y += lineHeight;
        g.DrawString($"Bolge      : {data.ZoneName}", fontContent, Brushes.Black, x, y);
        y += lineHeight;
        g.DrawString($"Giris      : {data.EntryDateTime:dd.MM.yyyy HH:mm}", fontContent, Brushes.Black, x, y);
        y += lineHeight + 4;

        // Cizgi
        g.DrawString("--------------------------------", fontContent, Brushes.Black, x, y);
        y += lineHeight;

        // Ucret bilgileri
        decimal kdvOrani = 0.20m;
        decimal kdvHaric = data.Fee / (1 + kdvOrani);
        decimal kdv = data.Fee - kdvHaric;

        g.DrawString($"Tutar (KDV Haric) : {kdvHaric:F2} TL", fontContent, Brushes.Black, x, y);
        y += lineHeight;
        g.DrawString($"KDV (%20)         : {kdv:F2} TL", fontContent, Brushes.Black, x, y);
        y += lineHeight;
        g.DrawString($"TOPLAM            : {data.Fee:F2} TL", fontBold, Brushes.Black, x, y);
        y += lineHeight + 4;

        // Cizgi
        g.DrawString("--------------------------------", fontContent, Brushes.Black, x, y);
        y += lineHeight;

        // Uyari
        g.DrawString("Lutfen degerli esyalarinizi", fontSubtitle, Brushes.Black, x, y);
        y += lineHeight;
        g.DrawString("aracinizda birakmayiniz.", fontSubtitle, Brushes.Black, x, y);
        y += lineHeight + 4;
        g.DrawString($"Operator: {data.OperatorName}", fontSubtitle, Brushes.Black, x, y);
        y += lineHeight + 4;

        if (maliyeLogo != null)
            g.DrawImage(maliyeLogo, 100, y, 60, 60);

        fontTitle.Dispose();
        fontSubtitle.Dispose();
        fontContent.Dispose();
        fontBold.Dispose();
    }

    private static void DrawExitReceipt(Graphics g, ReceiptData data)
    {
        var fontTitle = new Font("Arial", 10, FontStyle.Bold);
        var fontSubtitle = new Font("Arial", 8, FontStyle.Regular);
        var fontContent = new Font("Arial", 8, FontStyle.Regular);
        var fontBold = new Font("Arial", 8, FontStyle.Bold);

        float y = 10;
        float x = 10;
        float lineHeight = 16;

        // Logolar
        using var imarLogo = LoadLogo("IMAR LOGO.bmp");
        using var maliyeLogo = LoadLogo("MALIYE LOGO.bmp");
        if (imarLogo != null)
            g.DrawImage(imarLogo, 90, y, 80, 80);
        y += imarLogo != null ? 85 : 0;

        // Baslik
        g.DrawString("Kayseri Ulasim A.S.", fontTitle, Brushes.Black, x, y);
        y += lineHeight + 4;
        g.DrawString("Kapali Otopark", fontSubtitle, Brushes.Black, x, y);
        y += lineHeight;
        g.DrawString("Cikis Fisi", fontSubtitle, Brushes.Black, x, y);
        y += lineHeight + 8;

        // Cizgi
        g.DrawString("--------------------------------", fontContent, Brushes.Black, x, y);
        y += lineHeight;

        // Bilgiler
        g.DrawString($"Fis No     : {data.ReceiptNo}", fontContent, Brushes.Black, x, y);
        y += lineHeight;
        g.DrawString($"Tarih      : {data.DateTime:dd.MM.yyyy HH:mm}", fontContent, Brushes.Black, x, y);
        y += lineHeight;
        g.DrawString($"Plaka      : {data.Plate}", fontBold, Brushes.Black, x, y);
        y += lineHeight;
        g.DrawString($"Bolge      : {data.ZoneName}", fontContent, Brushes.Black, x, y);
        y += lineHeight;
        g.DrawString($"Giris      : {data.EntryDateTime:dd.MM.yyyy HH:mm}", fontContent, Brushes.Black, x, y);
        y += lineHeight;
        g.DrawString($"Cikis      : {data.ExitDateTime:dd.MM.yyyy HH:mm}", fontContent, Brushes.Black, x, y);
        y += lineHeight;

        // Sure
        var sure = (data.ExitDateTime ?? DateTime.Now) - data.EntryDateTime;
        g.DrawString($"Sure       : {(int)sure.TotalHours} saat {sure.Minutes} dk", fontContent, Brushes.Black, x, y);
        y += lineHeight + 4;

        // Cizgi
        g.DrawString("--------------------------------", fontContent, Brushes.Black, x, y);
        y += lineHeight;

        // Ucret
        g.DrawString($"Eski Borc         : {data.OldDebt:F2} TL", fontContent, Brushes.Black, x, y);
        y += lineHeight;
        g.DrawString($"Park Ucreti       : {data.Fee:F2} TL", fontContent, Brushes.Black, x, y);
        y += lineHeight;
        g.DrawString($"TOPLAM            : {(data.OldDebt + data.Fee):F2} TL", fontBold, Brushes.Black, x, y);
        y += lineHeight + 4;

        // Cizgi
        g.DrawString("--------------------------------", fontContent, Brushes.Black, x, y);
        y += lineHeight;

        g.DrawString($"Operator: {data.OperatorName}", fontSubtitle, Brushes.Black, x, y);
        y += lineHeight + 4;

        if (maliyeLogo != null)
            g.DrawImage(maliyeLogo, 100, y, 60, 60);

        fontTitle.Dispose();
        fontSubtitle.Dispose();
        fontContent.Dispose();
        fontBold.Dispose();
    }
}

public class ReceiptData
{
    public string ReceiptNo { get; set; } = "";
    public DateTime DateTime { get; set; } = System.DateTime.Now;
    public string Plate { get; set; } = "";
    public string ZoneName { get; set; } = "";
    public DateTime EntryDateTime { get; set; }
    public DateTime? ExitDateTime { get; set; }
    public decimal Fee { get; set; }
    public decimal OldDebt { get; set; }
    public string OperatorName { get; set; } = "";
}
