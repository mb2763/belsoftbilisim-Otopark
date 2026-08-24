using System.Net.Http.Json;

namespace Otopark.Api.Services;

// Request modelini net sen verdin:
public sealed class LoginRequest
{
    public string UserNameEmail { get; set; } = "";
    public string CompanyCode { get; set; } = "";
    public string Password { get; set; } = "";
    public long ZoneId { get; set; }
    public int LoginType { get; set; }
}

// Response örneğindeki ana yapı (Result içindeki kullanıcı)
public sealed class LoginUserDto
{
    public long Id { get; set; }
    public string NameSurname { get; set; } = "";
    public string UserName { get; set; } = "";
    public string CompanyName { get; set; } = "";
    public int LoginType { get; set; }
    public string LoginTypeText { get; set; } = "";
    public string UserType { get; set; } = "";
}

public sealed class LoginErrorObject
{
    public int Code { get; set; }
    public string? Message { get; set; }
}

public sealed class LoginResponse
{
    public List<LoginErrorObject>? Errors { get; set; }
    public object? Status { get; set; }
    public string? InvoiceNumber { get; set; }
    public long TaxNumber { get; set; }
    public LoginUserDto? Result { get; set; }
}

// Servis: burada NSwag client'ı çağıracağız
public sealed class AuthApiService
{
    private readonly HttpClient _http;

    public AuthApiService(HttpClient http)
    {
        _http = http;
    }

    /// <summary>
    /// KULLANICI MENU YETKISI SORGUSU (24.08.2026).
    ///
    /// "Web'de tanimla, exe'de uygula" kalibinin ikinci ornegi (birincisi
    /// GetAuthorizedZonesAsync). Sunucu VEW_USER_PRIVILEGE listesi doner;
    /// burada yalnizca istenen menu icin satir VAR MI diye bakilir.
    ///
    /// FAIL-OPEN DEGIL, FAIL-CLOSED: servis hatasi ya da eski sunucu surumunde
    /// false doner (dugme gizli kalir). Yetki isteyen bir ozellik icin dogru
    /// varsayilan budur; yoneticiler icin cagiran taraf zaten ayrica gecer.
    /// </summary>
    public async Task<bool> HasMenuPrivilegeAsync(long userId, int menuTypeId)
    {
        try
        {
            var url = $"UserPrivileges/GetPrivileges?userId={userId}";
            using var response = await _http.PostAsync(url, null);
            if (!response.IsSuccessStatusCode) return false;

            var json = await response.Content.ReadAsStringAsync();
            if (string.IsNullOrWhiteSpace(json)) return false;

            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array) return false;

            foreach (var el in doc.RootElement.EnumerateArray())
            {
                foreach (var prop in el.EnumerateObject())
                {
                    if (!prop.NameEquals("menuTypeId") &&
                        !string.Equals(prop.Name, "MenuTypeId", StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (prop.Value.TryGetInt32(out var deger) && deger == menuTypeId)
                        return true;
                }
            }
            return false;
        }
        catch { return false; }
    }

    public async Task<LoginResponse?> LoginAsync(LoginRequest req)
    {
        // Endpoint: /Login/LoginControl (POST)
        // Token yok → direkt çağrı.

        var url = "Login/LoginControl";

        using var response = await _http.PostAsJsonAsync(url, new
        {
            userNameEmail = req.UserNameEmail,
            companyCode = req.CompanyCode,
            password = req.Password,
            zoneId = req.ZoneId,
            loginType = req.LoginType
        });

        var json = await response.Content.ReadAsStringAsync();

        var options = new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        return System.Text.Json.JsonSerializer.Deserialize<LoginResponse>(json, options);
    }
}
