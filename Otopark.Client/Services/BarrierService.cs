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

    public static async Task<BarrierResult> OpenEntryGateAsync()
    {
        return await SendCommandAsync(EntryUrl, "Giris bariyeri");
    }

    public static async Task<BarrierResult> OpenExitGateAsync()
    {
        return await SendCommandAsync(ExitUrl, "Cikis bariyeri");
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
