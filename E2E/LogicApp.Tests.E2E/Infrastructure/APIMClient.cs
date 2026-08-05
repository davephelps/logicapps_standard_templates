// APIMClient -- HTTP client wrapper for hitting the APIM gateway from E2E tests. In the local /
// CI configuration the base URL points at the in-process ApimEmulator. When swapped to a real
// APIM dev tenant the emulator is replaced and only the BaseUrl + subscription key change.

using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;

namespace LogicApp.Tests.E2E.Infrastructure;

public sealed class APIMClient : IDisposable
{
    private readonly HttpClient _http;

    public string AssetUrl { get; }
    public string HealthUrl { get; }
    public string SubscriptionKey { get; }

    public APIMClient(string assetUrl, string healthUrl, string subscriptionKey)
    {
        AssetUrl = assetUrl;
        HealthUrl = healthUrl;
        SubscriptionKey = subscriptionKey;
        _http = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(2)
        };
    }

    public Task<HttpResponseMessage> PostAssetAsync(
        string jsonBody,
        string? subscriptionKeyOverride = null,
        IDictionary<string, string>? extraHeaders = null,
        string contentType = "application/json")
    {
        var key = subscriptionKeyOverride ?? SubscriptionKey;
        return SendAsync(HttpMethod.Post, AssetUrl, jsonBody, key, extraHeaders, contentType);
    }

    public Task<HttpResponseMessage> GetHealthAsync(string? subscriptionKeyOverride = null)
    {
        var key = subscriptionKeyOverride ?? SubscriptionKey;
        return SendAsync(HttpMethod.Get, HealthUrl, body: null, subscriptionKey: key);
    }

    public Task<HttpResponseMessage> SendRawAsync(HttpRequestMessage request) =>
        _http.SendAsync(request);

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string url,
        string? body,
        string? subscriptionKey,
        IDictionary<string, string>? extraHeaders = null,
        string contentType = "application/json")
    {
        using var request = new HttpRequestMessage(method, url);

        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, contentType);
        }

        if (!string.IsNullOrEmpty(subscriptionKey))
        {
            request.Headers.TryAddWithoutValidation("Ocp-Apim-Subscription-Key", subscriptionKey);
        }

        if (extraHeaders is not null)
        {
            foreach (var (k, v) in extraHeaders)
            {
                request.Headers.TryAddWithoutValidation(k, v);
            }
        }

        return await _http.SendAsync(request);
    }

    public void Dispose() => _http.Dispose();
}
