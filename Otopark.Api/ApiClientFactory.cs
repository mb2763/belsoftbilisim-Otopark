using System.Net.Http;

namespace Otopark.Api;

public static class ApiClientFactory
{
    public static HttpClient Create(string baseUrl)
    {
        var client = new HttpClient();
        client.BaseAddress = new Uri(baseUrl);
       client.Timeout = System.TimeSpan.FromSeconds(30);
        return client;
    }
}
