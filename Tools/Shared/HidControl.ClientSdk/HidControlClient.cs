using System.Net.Http.Json;
using System.Text.Json;

namespace HidControl.ClientSdk;

// Step 1: Minimal HTTP client wrapper for the shared contracts.
/// <summary>
/// Client for HID control API.
/// </summary>
public sealed class HidControlClient
{
    private readonly HttpClient _http;
    private readonly JsonSerializerOptions _json;

    /// <summary>
    /// Executes HidControlClient.
    /// </summary>
    /// <param name="http">The http.</param>
    /// <param name="baseUri">The baseUri.</param>
    /// <param name="json">The json.</param>
    public HidControlClient(HttpClient http, Uri baseUri, JsonSerializerOptions? json = null)
    {
        _http = http;
        _http.BaseAddress ??= baseUri;
        BaseUri = baseUri;
        _json = json ?? new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };
    }

    public Uri BaseUri { get; }

    public Task<T?> GetAsync<T>(string path, CancellationToken ct = default)
        => _http.GetFromJsonAsync<T>(Normalize(path), _json, ct);

    /// <summary>
    /// Sends a GET request with retry/backoff.
    /// </summary>
    /// <typeparam name="T">Response type.</typeparam>
    /// <param name="path">The path.</param>
    /// <param name="maxAttempts">Maximum attempts.</param>
    /// <param name="baseDelayMs">Initial delay in milliseconds.</param>
    /// <param name="maxDelayMs">Maximum delay in milliseconds.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>Result.</returns>
    public Task<T?> GetWithRetryAsync<T>(string path, int maxAttempts = 3, int baseDelayMs = 200, int maxDelayMs = 2000, CancellationToken ct = default)
        => RetryPolicy.ExecuteAsync(_ => GetAsync<T>(path, ct), maxAttempts, baseDelayMs, maxDelayMs, ct);

    public async Task<TResponse?> PostAsync<TRequest, TResponse>(string path, TRequest payload, CancellationToken ct = default)
    {
        using var response = await _http.PostAsJsonAsync(Normalize(path), payload, _json, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<TResponse>(_json, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends a POST request with retry/backoff.
    /// </summary>
    /// <typeparam name="TRequest">Request type.</typeparam>
    /// <typeparam name="TResponse">Response type.</typeparam>
    /// <param name="path">The path.</param>
    /// <param name="payload">The payload.</param>
    /// <param name="maxAttempts">Maximum attempts.</param>
    /// <param name="baseDelayMs">Initial delay in milliseconds.</param>
    /// <param name="maxDelayMs">Maximum delay in milliseconds.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>Result.</returns>
    public Task<TResponse?> PostWithRetryAsync<TRequest, TResponse>(string path, TRequest payload, int maxAttempts = 3, int baseDelayMs = 200, int maxDelayMs = 2000, CancellationToken ct = default)
        => RetryPolicy.ExecuteAsync(_ => PostAsync<TRequest, TResponse>(path, payload, ct), maxAttempts, baseDelayMs, maxDelayMs, ct);

    public async Task<HttpResponseMessage> PostAsync<TRequest>(string path, TRequest payload, CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync(Normalize(path), payload, _json, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return response;
    }

    /// <summary>
    /// Executes PostAsync.
    /// </summary>
    /// <param name="path">The path.</param>
    /// <param name="ct">The ct.</param>
    /// <returns>Result.</returns>
    public Task<HttpResponseMessage> PostAsync(string path, CancellationToken ct = default)
        => _http.PostAsync(Normalize(path), null, ct);

    /// <summary>
    /// Gets raw.
    /// </summary>
    /// <param name="path">The path.</param>
    /// <param name="ct">The ct.</param>
    /// <returns>Result.</returns>
    public Task<HttpResponseMessage> GetRawAsync(string path, CancellationToken ct = default)
        => _http.GetAsync(Normalize(path), ct);

    /// <summary>
    /// Executes Normalize.
    /// </summary>
    /// <param name="path">The path.</param>
    /// <returns>Result.</returns>
    private static string Normalize(string path)
        => path.StartsWith("/", StringComparison.Ordinal) ? path : $"/{path}";
}
