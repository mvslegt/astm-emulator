using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Hl7Emulator.Core.Fhir;

public sealed record FhirResponse(int StatusCode, string Reason, string Headers, string Body, TimeSpan Elapsed, string? Error)
{
    public bool IsSuccess => Error == null && StatusCode is >= 200 and < 300;
    public string Summary => Error ?? $"{StatusCode} {Reason} ({Elapsed.TotalMilliseconds:0} ms)";
}

/// <summary>Sends FHIR REST requests (JSON) to the LIS.</summary>
public sealed class FhirClient : IDisposable
{
    private readonly HttpClient _http = new();

    public static string Combine(string baseUrl, string path)
    {
        if (Uri.TryCreate(path, UriKind.Absolute, out var abs) && abs.Scheme.StartsWith("http")) return path;
        return baseUrl.TrimEnd('/') + (string.IsNullOrEmpty(path) ? "" : "/" + path.TrimStart('/'));
    }

    public async Task<FhirResponse> SendAsync(string baseUrl, FhirRequest req, string? bearerToken, TimeSpan timeout, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var url = Combine(baseUrl, req.Path);
            using var msg = new HttpRequestMessage(new HttpMethod(req.Method.ToUpperInvariant()), url);
            msg.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/fhir+json"));
            if (!string.IsNullOrWhiteSpace(bearerToken))
                msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken.Trim());
            if (!string.IsNullOrWhiteSpace(req.Body) && req.Method.ToUpperInvariant() is not ("GET" or "DELETE"))
                msg.Content = new StringContent(req.Body, Encoding.UTF8, "application/fhir+json");

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            using var resp = await _http.SendAsync(msg, cts.Token);
            var body = await resp.Content.ReadAsStringAsync(cts.Token);
            var headers = string.Join(Environment.NewLine,
                resp.Headers.Concat(resp.Content.Headers).Select(h => $"{h.Key}: {string.Join(", ", h.Value)}"));
            return new FhirResponse((int)resp.StatusCode, resp.ReasonPhrase ?? "", headers, Pretty(body), sw.Elapsed, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new FhirResponse(0, "", "", "", sw.Elapsed, $"Timeout after {timeout.TotalSeconds:0} s");
        }
        catch (Exception ex)
        {
            return new FhirResponse(0, "", "", "", sw.Elapsed, ex.InnerException?.Message ?? ex.Message);
        }
    }

    /// <summary>Re-indents JSON for display; returns the input unchanged if it is not JSON.</summary>
    public static string Pretty(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return body;
        try { return JsonNode.Parse(body)?.ToJsonString(FhirBuilder.JsonOptions) ?? body; }
        catch (JsonException) { return body; }
    }

    public void Dispose() => _http.Dispose();
}
