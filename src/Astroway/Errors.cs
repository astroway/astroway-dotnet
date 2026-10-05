using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;

namespace Astroway;

/// <summary>A non-2xx answer. Catch a subclass for one kind, or this for all.</summary>
public class AstrowayException : Exception
{
    /// <summary>Creates an error from parsed response fields.</summary>
    public AstrowayException(string message, int statusCode, string? code = null, string? requestId = null,
        TimeSpan? retryAfter = null, int? creditsRemaining = null, string? body = null) : base(message)
    {
        StatusCode = statusCode;
        Code = code;
        RequestId = requestId;
        RetryAfter = retryAfter;
        CreditsRemaining = creditsRemaining;
        Body = body;
    }

    /// <summary>HTTP status.</summary>
    public int StatusCode { get; }
    /// <summary>Machine code from the error body, such as INVALID_INPUT or OUT_OF_CREDITS.</summary>
    public string? Code { get; }
    /// <summary>X-Request-Id; quote it to support.</summary>
    public string? RequestId { get; }
    /// <summary>Retry-After, when sent.</summary>
    public TimeSpan? RetryAfter { get; }
    /// <summary>X-Credits-Remaining, when sent.</summary>
    public int? CreditsRemaining { get; }
    /// <summary>Raw response body.</summary>
    public string? Body { get; }

    private static readonly HashSet<string> QuotaCodes = new() { "OUT_OF_CREDITS", "QUOTA_EXCEEDED", "CREDIT_LIMIT_REACHED" };
    private static readonly HashSet<string> CalculationCodes = new() { "CALCULATION_ERROR", "EPHEMERIS_ERROR" };

    internal static AstrowayException From(HttpResponseMessage response, byte[] raw)
    {
        var status = (int)response.StatusCode;
        var body = System.Text.Encoding.UTF8.GetString(raw);
        string? code = null;
        var message = response.ReasonPhrase ?? status.ToString(CultureInfo.InvariantCulture);
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("error", out var err)
                && err.ValueKind == JsonValueKind.Object)
            {
                if (err.TryGetProperty("code", out var c) && c.ValueKind != JsonValueKind.Null) code = c.ToString();
                if (err.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String) message = m.GetString()!;
            }
        }
        catch (JsonException)
        {
        }

        var requestId = AstrowayClient.Header(response, "X-Request-Id");
        var credits = AstrowayClient.IntHeader(response, "X-Credits-Remaining");
        var retryAfter = ParseRetryAfter(response);

        // Code first: quota and calculation failures ride on more than one status.
        if (code is not null && QuotaCodes.Contains(code)) return new QuotaExceededException(message, status, code, requestId, retryAfter, credits, body);
        if (code is not null && CalculationCodes.Contains(code)) return new CalculationException(message, status, code, requestId, retryAfter, credits, body);
        return status switch
        {
            400 => new BadRequestException(message, status, code, requestId, retryAfter, credits, body),
            401 => new AuthenticationException(message, status, code, requestId, retryAfter, credits, body),
            402 => new QuotaExceededException(message, status, code, requestId, retryAfter, credits, body),
            403 => new PermissionDeniedException(message, status, code, requestId, retryAfter, credits, body),
            404 => new NotFoundException(message, status, code, requestId, retryAfter, credits, body),
            422 => new UnprocessableEntityException(message, status, code, requestId, retryAfter, credits, body),
            429 => new RateLimitException(message, status, code, requestId, retryAfter, credits, body),
            >= 500 => new InternalServerException(message, status, code, requestId, retryAfter, credits, body),
            _ => new AstrowayException(message, status, code, requestId, retryAfter, credits, body),
        };
    }

    private static TimeSpan? ParseRetryAfter(HttpResponseMessage response)
    {
        var ra = response.Headers.RetryAfter;
        if (ra is null) return null;
        if (ra.Delta is { } d) return d;
        if (ra.Date is { } date)
        {
            var wait = date - DateTimeOffset.UtcNow;
            return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
        }
        return null;
    }

    /// <inheritdoc />
    public override string ToString() =>
        $"{GetType().Name}: {StatusCode} {Message}" + (Code is null ? "" : $" ({Code})") + (RequestId is null ? "" : $" request_id={RequestId}");
}

#pragma warning disable CS1591
public sealed class BadRequestException : AstrowayException
{
    public BadRequestException(string m, int s, string? c, string? r, TimeSpan? ra, int? cr, string? b) : base(m, s, c, r, ra, cr, b) { }
}

public sealed class AuthenticationException : AstrowayException
{
    public AuthenticationException(string m, int s, string? c, string? r, TimeSpan? ra, int? cr, string? b) : base(m, s, c, r, ra, cr, b) { }
}

public sealed class PermissionDeniedException : AstrowayException
{
    public PermissionDeniedException(string m, int s, string? c, string? r, TimeSpan? ra, int? cr, string? b) : base(m, s, c, r, ra, cr, b) { }
}

public sealed class NotFoundException : AstrowayException
{
    public NotFoundException(string m, int s, string? c, string? r, TimeSpan? ra, int? cr, string? b) : base(m, s, c, r, ra, cr, b) { }
}

public sealed class UnprocessableEntityException : AstrowayException
{
    public UnprocessableEntityException(string m, int s, string? c, string? r, TimeSpan? ra, int? cr, string? b) : base(m, s, c, r, ra, cr, b) { }
}

/// <summary>Still 429 after the retries.</summary>
public sealed class RateLimitException : AstrowayException
{
    public RateLimitException(string m, int s, string? c, string? r, TimeSpan? ra, int? cr, string? b) : base(m, s, c, r, ra, cr, b) { }
}

/// <summary>Out of credits: waiting a few seconds will not help, top up or wait for the period to reset.</summary>
public sealed class QuotaExceededException : AstrowayException
{
    public QuotaExceededException(string m, int s, string? c, string? r, TimeSpan? ra, int? cr, string? b) : base(m, s, c, r, ra, cr, b) { }
}

public sealed class CalculationException : AstrowayException
{
    public CalculationException(string m, int s, string? c, string? r, TimeSpan? ra, int? cr, string? b) : base(m, s, c, r, ra, cr, b) { }
}

public sealed class InternalServerException : AstrowayException
{
    public InternalServerException(string m, int s, string? c, string? r, TimeSpan? ra, int? cr, string? b) : base(m, s, c, r, ra, cr, b) { }
}
#pragma warning restore CS1591

/// <summary>A network failure after all retries.</summary>
public sealed class AstrowayConnectionException : Exception
{
    /// <summary>Wraps the transport error.</summary>
    public AstrowayConnectionException(string message, Exception inner) : base(message, inner) { }
}
