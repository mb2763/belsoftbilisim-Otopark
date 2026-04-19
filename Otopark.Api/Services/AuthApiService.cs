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
