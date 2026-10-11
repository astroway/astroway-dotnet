using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Astroway;

/// <summary>Client for the AstroWay API. Thread-safe; create one and reuse it.</summary>
/// <example>
/// <code>
/// using var aw = new AstrowayClient(Environment.GetEnvironmentVariable("ASTROWAY_API_KEY")!);
/// var res = await aw.Chart.ComputeAsync(new { date = "1990-07-14", time = "14:30:00",
///     timezoneOffset = 3, latitude = 50.45, longitude = 30.52 });
/// double asc = res.Data.GetProperty("houses").GetProperty("ascendant").GetDouble();
/// </code>
/// </example>
public sealed partial class AstrowayClient : IDisposable
{
    /// <summary>SDK version, sent in the User-Agent.</summary>
    public const string SdkVersion = "0.2.0";

    /// <summary>Production API root.</summary>
    public const string DefaultBaseUrl = "https://api.astroway.info/v1";

    internal static readonly JsonSerializerOptions BodyJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly HashSet<int> Retryable = new() { 408, 409, 429, 500, 502, 503, 504 };
    private static readonly Random Jitter = new();

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly AstrowayClientOptions _options;
    private readonly string _apiKey;
    private readonly string _baseUrl;

    /// <param name="apiKey">Your key from https://api.astroway.info/dashboard/.</param>
    /// <param name="options">Retries, timeout, base URL, your own HttpClient.</param>
    public AstrowayClient(string apiKey, AstrowayClientOptions? options = null)
    {
        _apiKey = apiKey ?? throw new ArgumentNullException(nameof(apiKey));
        _options = options ?? new AstrowayClientOptions();
        _baseUrl = (_options.BaseUrl ?? DefaultBaseUrl).TrimEnd('/');
        if (_options.HttpClient is { } shared)
        {
            _http = shared;
        }
        else
        {
            _http = new HttpClient { Timeout = _options.Timeout };
            _ownsHttp = true;
        }
        InitNamespaces();
    }

    /// <summary>Sends one request. <paramref name="path"/> is relative to the base URL ("/chart").</summary>
    public async Task<AstrowayResponse> RequestAsync(HttpMethod method, string path, object? body = null,
        RequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        var payload = body is null ? null : JsonSerializer.SerializeToUtf8Bytes(body, body.GetType(), BodyJson);
        var url = _baseUrl + path + QueryString(options?.Query);
        var idempotencyKey = options?.IdempotencyKey;
        if (idempotencyKey is null && _options.Idempotency && method == HttpMethod.Post
            && !(options?.Headers?.Keys.Any(k => string.Equals(k, "Idempotency-Key", StringComparison.OrdinalIgnoreCase)) ?? false))
        {
            idempotencyKey = Guid.NewGuid().ToString("N");
        }

        for (var attempt = 0; ; attempt++)
        {
            using var request = BuildRequest(method, url, payload, options, idempotencyKey);
            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
            {
                if (attempt < _options.MaxRetries)
                {
                    await Task.Delay(Backoff(attempt, null), cancellationToken).ConfigureAwait(false);
                    continue;
                }
                throw new AstrowayConnectionException($"Network error calling {path}: {ex.Message}", ex);
            }

            using (response)
            {
                var raw = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                var status = (int)response.StatusCode;
                if (status >= 200 && status < 300)
                {
                    return new AstrowayResponse(Unwrap(raw), status, response.Headers,
                        Header(response, "X-Request-Id"), IntHeader(response, "X-Credits-Remaining"));
                }
                var error = AstrowayException.From(response, raw);
                if (Retryable.Contains(status) && attempt < _options.MaxRetries && error is not QuotaExceededException)
                {
                    await Task.Delay(Backoff(attempt, error.RetryAfter), cancellationToken).ConfigureAwait(false);
                    continue;
                }
                throw error;
            }
        }
    }

    /// <summary>POST with a JSON body.</summary>
    public Task<AstrowayResponse> PostAsync(string path, object? body = null, RequestOptions? options = null,
        CancellationToken cancellationToken = default) => RequestAsync(HttpMethod.Post, path, body, options, cancellationToken);

    /// <summary>GET without a body.</summary>
    public Task<AstrowayResponse> GetAsync(string path, RequestOptions? options = null,
        CancellationToken cancellationToken = default) => RequestAsync(HttpMethod.Get, path, null, options, cancellationToken);

    /// <summary>
    /// Draws a chart you already hold (the <c>Data</c> of <c>Chart.ComputeAsync</c>) for 1 credit instead of 10:
    /// nothing is computed again. <paramref name="kind"/> is "wheel" or "aspect-grid". The SVG is byte for byte
    /// the one /render/wheel-western returns from birth data.
    /// </summary>
    public Task<AstrowayResponse> DrawChartAsync(object chart, string kind = "wheel", object? renderOptions = null,
        RequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        var path = kind == "aspect-grid" ? "/render/aspect-grid" : "/render/wheel-western";
        var body = new Dictionary<string, object?> { ["chart"] = chart };
        if (renderOptions is not null) body["options"] = renderOptions;
        return PostAsync(path, body, options, cancellationToken);
    }

    /// <summary>Draws two charts you already hold as a bi-wheel for 1 credit.</summary>
    public Task<AstrowayResponse> DrawBiWheelAsync(object natal, object outer, object? renderOptions = null,
        RequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        var body = new Dictionary<string, object?>
        {
            ["natal"] = new Dictionary<string, object?> { ["chart"] = natal },
            ["outer"] = new Dictionary<string, object?> { ["chart"] = outer },
        };
        if (renderOptions is not null) body["options"] = renderOptions;
        return PostAsync("/render/bi-wheel", body, options, cancellationToken);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }

    private HttpRequestMessage BuildRequest(HttpMethod method, string url, byte[]? payload, RequestOptions? options, string? idempotencyKey)
    {
        var request = new HttpRequestMessage(method, url);
        if (payload is not null)
        {
            request.Content = new ByteArrayContent(payload);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }
        if (options?.Headers is { } extra)
        {
            foreach (var kv in extra) request.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
        }
        request.Headers.TryAddWithoutValidation("User-Agent", $"astroway-sdk-dotnet/{SdkVersion} ({System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription})");
        request.Headers.TryAddWithoutValidation("X-Astroway-Channel", "sdk-dotnet");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (_options.BearerAuth)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        else
            request.Headers.TryAddWithoutValidation("X-Api-Key", _apiKey);
        if (idempotencyKey is not null)
        {
            request.Headers.Remove("Idempotency-Key");
            request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        }
        return request;
    }

    // Full-jitter exponential backoff; Retry-After wins when the server sends it.
    private TimeSpan Backoff(int attempt, TimeSpan? retryAfter)
    {
        if (retryAfter is { } ra) return ra < _options.MaxRetryDelay ? ra : _options.MaxRetryDelay;
        var upper = Math.Min(_options.MaxRetryDelay.TotalMilliseconds, _options.RetryBaseDelay.TotalMilliseconds * Math.Pow(2, attempt));
        double r;
        lock (Jitter) r = Jitter.NextDouble();
        return TimeSpan.FromMilliseconds(r * upper);
    }

    private static string QueryString(IDictionary<string, string>? query)
    {
        if (query is null || query.Count == 0) return "";
        return "?" + string.Join("&", query.Select(kv => Uri.EscapeDataString(kv.Key) + "=" + Uri.EscapeDataString(kv.Value)));
    }

    private static JsonElement Unwrap(byte[] raw)
    {
        if (raw.Length == 0) return default;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data)) return data.Clone();
            return root.Clone();
        }
        catch (JsonException)
        {
            using var doc = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(Encoding.UTF8.GetString(raw)));
            return doc.RootElement.Clone();
        }
    }

    internal static string? Header(HttpResponseMessage r, string name) =>
        r.Headers.TryGetValues(name, out var v) ? v.FirstOrDefault() : null;

    internal static int? IntHeader(HttpResponseMessage r, string name) =>
        int.TryParse(Header(r, name), out var n) ? n : null;
}

/// <summary>Client-wide settings.</summary>
public sealed class AstrowayClientOptions
{
    /// <summary>API root; defaults to production.</summary>
    public string? BaseUrl { get; set; }
    /// <summary>Your own HttpClient (proxies, handlers, IHttpClientFactory). The client does not dispose it.</summary>
    public HttpClient? HttpClient { get; set; }
    /// <summary>Timeout of the HttpClient the SDK creates. Default 30 s.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);
    /// <summary>Retries on 408/409/429/5xx and network errors. Default 2, 0 disables.</summary>
    public int MaxRetries { get; set; } = 2;
    /// <summary>First backoff step. Default 250 ms.</summary>
    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromMilliseconds(250);
    /// <summary>Longest wait between retries. Default 30 s.</summary>
    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromSeconds(30);
    /// <summary>Send the key as "Authorization: Bearer" instead of "X-Api-Key".</summary>
    public bool BearerAuth { get; set; }
    /// <summary>Attach an Idempotency-Key to every POST, kept across retries. Default true.</summary>
    public bool Idempotency { get; set; } = true;
}

/// <summary>Per-call settings.</summary>
public sealed class RequestOptions
{
    /// <summary>Query parameters such as lang, fields, precision.</summary>
    public IDictionary<string, string>? Query { get; set; }
    /// <summary>Extra request headers.</summary>
    public IDictionary<string, string>? Headers { get; set; }
    /// <summary>Idempotency-Key for this POST, so a retried call is not charged twice.</summary>
    public string? IdempotencyKey { get; set; }
}

/// <summary>A successful answer with the { ok, data, error } envelope unwrapped.</summary>
public sealed class AstrowayResponse
{
    internal AstrowayResponse(JsonElement data, int statusCode, HttpResponseHeaders headers, string? requestId, int? creditsRemaining)
    {
        Data = data;
        StatusCode = statusCode;
        Headers = headers;
        RequestId = requestId;
        CreditsRemaining = creditsRemaining;
    }

    /// <summary>The "data" member of the envelope, or the whole body when there is none.</summary>
    public JsonElement Data { get; }
    /// <summary>HTTP status.</summary>
    public int StatusCode { get; }
    /// <summary>Response headers.</summary>
    public HttpResponseHeaders Headers { get; }
    /// <summary>X-Request-Id; quote it to support.</summary>
    public string? RequestId { get; }
    /// <summary>X-Credits-Remaining, when sent.</summary>
    public int? CreditsRemaining { get; }

    private static readonly JsonSerializerOptions ReadJson = new(JsonSerializerDefaults.Web);

    /// <summary>Deserializes <see cref="Data"/> into your own type (camelCase JSON, case-insensitive).</summary>
    public T? Deserialize<T>(JsonSerializerOptions? options = null) => Data.Deserialize<T>(options ?? ReadJson);
}
