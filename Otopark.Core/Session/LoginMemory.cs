using System;
using System.IO;

namespace Otopark.Core.Session;

public static class LoginMemory
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Otopark", "login_memory.txt");

    public static string UserNameEmail { get; set; } = "";
    public static string CompanyCode { get; set; } = "";
    public static long? SelectedZoneId { get; set; }

    static LoginMemory()
    {
        Load();
    }

    public static void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(FilePath)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(FilePath,
                $"{UserNameEmail}\n{CompanyCode}\n{SelectedZoneId}");
        }
        catch { }
    }

    private static void Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            var lines = File.ReadAllLines(FilePath);
            if (lines.Length >= 1) UserNameEmail = lines[0];
            if (lines.Length >= 2) CompanyCode = lines[1];
            if (lines.Length >= 3 && long.TryParse(lines[2], out var zoneId))
                SelectedZoneId = zoneId;
        }
        catch { }
    }
}
