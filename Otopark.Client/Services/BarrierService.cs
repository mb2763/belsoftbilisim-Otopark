using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Otopark.Core.Services;

namespace Otopark.Client.Services;

public record BarrierResult(bool Success, string Message);

public static class BarrierService
{
    private static readonly HttpClient Http = new HttpClient(new HttpClientHandler
    {
        Credentials = new NetworkCredential(
            AppConfig.Configuration["Camera:Username"] ?? "admin",
            AppConfig.Configuration["Camera:Password"] ?? "admin")
    })
    {
        Timeout = TimeSpan.FromSeconds(5)
    };

    private static string EntryUrl => AppConfig.Configuration["Barrier:EntryCommandUrl"] ?? "";
    private static string ExitUrl => AppConfig.Configuration["Barrier:ExitCommandUrl"] ?? "";
    private static int DelayMs => int.TryParse(AppConfig.Configuration["Barrier:DelayMs"], out var d) ? d : 100;

    // FIX 1 — Bariyer cooldown'u: her bariyer (Giris/Cikis) icin son acma'dan sonra
    // belirli sn icinde yeni acma komutu YUTULUR (false don, "cooldown" log'la).
    // Plakaya bagli degil — kuresel: aynı araba farkli plakalarla taninsa
    // bile bariyer dakikada sadece N kez acilir.
    // appsettings.json "Barrier:CooldownSeconds" ile ayarlanir (default 15 sn).
    private static int CooldownSeconds => int.TryParse(AppConfig.Configuration["Barrier:CooldownSeconds"], out var c) ? c : 15;
    private static DateTime _lastEntryOpenUtc = DateTime.MinValue;
    private static DateTime _lastExitOpenUtc = DateTime.MinValue;
    private static readonly object _cooldownLock = new();

    public static async Task<BarrierResult> OpenEntryGateAsync()
    {
        if (!TryEnterCooldown(isEntry: true, out int remaining))
            return new BarrierResult(false, $"Giris bariyeri: cooldown ({remaining} sn kaldi) — komut yutuldu.");
        return await SendCommandAsync(EntryUrl, "Giris bariyeri");
    }

    public static async Task<BarrierResult> OpenExitGateAsync()
    {
        if (!TryEnterCooldown(isEntry: false, out int remaining))
            return new BarrierResult(false, $"Cikis bariyeri: cooldown ({remaining} sn kaldi) — komut yutuldu.");
        return await SendCommandAsync(ExitUrl, "Cikis bariyeri");
    }

    private static bool TryEnterCooldown(bool isEntry, out int remainingSec)
    {
        int cd = Math.Max(0, CooldownSeconds);
        if (cd <= 0) { remainingSec = 0; return true; }
        lock (_cooldownLock)
        {
            DateTime last = isEntry ? _lastEntryOpenUtc : _lastExitOpenUtc;
            var elapsed = DateTime.UtcNow - last;
            if (elapsed.TotalSeconds < cd)
            {
                remainingSec = (int)Math.Ceiling(cd - elapsed.TotalSeconds);
                return false;
            }
            if (isEntry) _lastEntryOpenUtc = DateTime.UtcNow;
            else _lastExitOpenUtc = DateTime.UtcNow;
            remainingSec = 0;
            return true;
        }
    }

    private static async Task<BarrierResult> SendCommandAsync(string url, string gateName)
    {
        if (string.IsNullOrWhiteSpace(url))
            return new BarrierResult(false, $"{gateName}: URL yapilandirilmamis.");

        try
        {
            if (DelayMs > 0)
                await Task.Delay(DelayMs);

            using var response = await Http.GetAsync(url);

            if (response.IsSuccessStatusCode)
                return new BarrierResult(true, $"{gateName} acildi.");

            return new BarrierResult(false, $"{gateName} acilamadi. HTTP {(int)response.StatusCode}");
        }
        catch (TaskCanceledException)
        {
            return new BarrierResult(false, $"{gateName}: Baglanti zaman asimina ugradi.");
        }
        catch (HttpRequestException ex)
        {
            return new BarrierResult(false, $"{gateName}: Baglanti hatasi - {ex.Message}");
        }
        catch (Exception ex)
        {
            return new BarrierResult(false, $"{gateName}: {ex.Message}");
        }
    }
}
