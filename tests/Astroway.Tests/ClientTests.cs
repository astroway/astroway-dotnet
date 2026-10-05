using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Astroway.Tests;

public class ClientTests
{
    private sealed class Stub : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, string?, HttpResponseMessage> _reply;
        public readonly List<(HttpRequestMessage Req, string? Body)> Calls = new();
        public Stub(Func<HttpRequestMessage, string?, HttpResponseMessage> reply) => _reply = reply;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            Calls.Add((request, body));
            return _reply(request, body);
        }
    }

    private static HttpResponseMessage Json(int status, string json, params (string, string)[] headers)
    {
        var r = new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        foreach (var (k, v) in headers) r.Headers.TryAddWithoutValidation(k, v);
        return r;
    }

    private static AstrowayClient Client(Stub stub, int retries = 2) => new("aw_test_key", new AstrowayClientOptions
    {
        HttpClient = new HttpClient(stub), BaseUrl = "https://example.test/v1", MaxRetries = retries,
        RetryBaseDelay = TimeSpan.FromMilliseconds(1),
    });

    [Fact]
    public async Task UnwrapsEnvelopeAndSendsBodyQueryAndHeaders()
    {
        var stub = new Stub((_, _) => Json(200, "{\"ok\":true,\"data\":{\"houses\":{\"ascendant\":212.09}}}",
            ("X-Request-Id", "req_1"), ("X-Credits-Remaining", "9980")));
        var res = await Client(stub).Chart.ComputeAsync(new { date = "1990-07-14", timezoneOffset = 3, houseSystem = (string?)null },
            new RequestOptions { Query = new Dictionary<string, string> { ["lang"] = "uk" } });

        Assert.Equal(212.09, res.Data.GetProperty("houses").GetProperty("ascendant").GetDouble());
        Assert.Equal("req_1", res.RequestId);
        Assert.Equal(9980, res.CreditsRemaining);
        var (req, body) = stub.Calls.Single();
        Assert.Equal("https://example.test/v1/chart?lang=uk", req.RequestUri!.ToString());
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Equal("aw_test_key", req.Headers.GetValues("X-Api-Key").Single());
        Assert.Equal("sdk-dotnet", req.Headers.GetValues("X-Astroway-Channel").Single());
        Assert.Equal("{\"date\":\"1990-07-14\",\"timezoneOffset\":3}", body);
    }

    private sealed record Birth(string Date, double TimezoneOffset);

    [Fact]
    public async Task PascalCaseRecordsGoOutCamelCaseAndDeserializeBack()
    {
        var stub = new Stub((_, _) => Json(200, "{\"ok\":true,\"data\":{\"date\":\"x\",\"timezoneOffset\":2}}"));
        var res = await Client(stub).PostAsync("/chart", new Birth("1990-07-14", 3));
        Assert.Equal("{\"date\":\"1990-07-14\",\"timezoneOffset\":3}", stub.Calls.Single().Body);
        Assert.Equal(new Birth("x", 2), res.Deserialize<Birth>());
    }

    [Theory]
    [InlineData(400, "INVALID_INPUT", typeof(BadRequestException))]
    [InlineData(401, "", typeof(AuthenticationException))]
    [InlineData(402, "", typeof(QuotaExceededException))]
    [InlineData(403, "", typeof(PermissionDeniedException))]
    [InlineData(404, "", typeof(NotFoundException))]
    [InlineData(422, "", typeof(UnprocessableEntityException))]
    [InlineData(429, "OUT_OF_CREDITS", typeof(QuotaExceededException))]
    [InlineData(400, "CALCULATION_ERROR", typeof(CalculationException))]
    [InlineData(418, "", typeof(AstrowayException))]
    public async Task MapsErrors(int status, string code, Type expected)
    {
        var stub = new Stub((_, _) => Json(status, $"{{\"ok\":false,\"error\":{{\"code\":\"{code}\",\"message\":\"nope\"}}}}", ("X-Request-Id", "req_e")));
        var ex = await Assert.ThrowsAnyAsync<AstrowayException>(() => Client(stub, retries: 0).PostAsync("/chart", new { }));
        Assert.IsType(expected, ex);
        Assert.Equal("nope", ex.Message);
        Assert.Equal("req_e", ex.RequestId);
        Assert.Equal(status, ex.StatusCode);
    }

    [Fact]
    public async Task RetriesKeepOneIdempotencyKey()
    {
        var n = 0;
        var stub = new Stub((_, _) => ++n < 3 ? Json(503, "{}", ("Retry-After", "0")) : Json(200, "{\"ok\":true,\"data\":1}"));
        await Client(stub).PostAsync("/chart", new { });
        var keys = stub.Calls.Select(c => c.Req.Headers.GetValues("Idempotency-Key").Single()).Distinct().ToList();
        Assert.Equal(3, stub.Calls.Count);
        Assert.Single(keys);
    }

    [Fact]
    public async Task GivesUpAfterMaxRetriesAndDoesNotRetryQuota()
    {
        var a = new Stub((_, _) => Json(500, "{}"));
        await Assert.ThrowsAsync<InternalServerException>(() => Client(a).PostAsync("/chart"));
        Assert.Equal(3, a.Calls.Count);

        var b = new Stub((_, _) => Json(429, "{\"ok\":false,\"error\":{\"code\":\"OUT_OF_CREDITS\",\"message\":\"top up\"}}"));
        await Assert.ThrowsAsync<QuotaExceededException>(() => Client(b).PostAsync("/chart"));
        Assert.Single(b.Calls);
    }

    [Fact]
    public async Task GetHasNoIdempotencyKeyAndBearerAuthWorks()
    {
        var stub = new Stub((_, _) => Json(200, "{\"ok\":true,\"data\":{}}"));
        using var aw = new AstrowayClient("k", new AstrowayClientOptions { HttpClient = new HttpClient(stub), BearerAuth = true });
        await aw.Health.ComputeAsync();
        var req = stub.Calls.Single().Req;
        Assert.False(req.Headers.Contains("Idempotency-Key"));
        Assert.False(req.Headers.Contains("X-Api-Key"));
        Assert.Equal("Bearer k", req.Headers.Authorization!.ToString());
        Assert.Equal(HttpMethod.Get, req.Method);
    }

    [Fact]
    public async Task DrawChartSendsTheChartAsIs()
    {
        var stub = new Stub((_, _) => Json(200, "{\"ok\":true,\"data\":{\"svg\":\"<svg/>\"}}"));
        var aw = Client(stub);
        using var chart = JsonDocument.Parse("{\"planets\":[]}");
        await aw.DrawChartAsync(chart.RootElement, "aspect-grid", new { theme = "css" });
        await aw.DrawBiWheelAsync(new { a = 1 }, new { b = 2 });

        Assert.EndsWith("/render/aspect-grid", stub.Calls[0].Req.RequestUri!.AbsolutePath);
        Assert.Equal("{\"chart\":{\"planets\":[]},\"options\":{\"theme\":\"css\"}}", stub.Calls[0].Body);
        Assert.EndsWith("/render/bi-wheel", stub.Calls[1].Req.RequestUri!.AbsolutePath);
        Assert.Equal("{\"natal\":{\"chart\":{\"a\":1}},\"outer\":{\"chart\":{\"b\":2}}}", stub.Calls[1].Body);
    }

    [Fact]
    public async Task CancellationStopsRetrying()
    {
        var stub = new Stub((_, _) => Json(503, "{}", ("Retry-After", "10")));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var started = DateTime.UtcNow;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Client(stub).PostAsync("/chart", null, null, cts.Token));
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(3));
    }
}
