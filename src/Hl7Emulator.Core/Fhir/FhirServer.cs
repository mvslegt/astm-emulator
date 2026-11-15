using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ViperAstmEmulator.Core.Astm;

namespace Hl7Emulator.Core.Fhir;

public sealed record FhirExchange(DateTime Time, string Method, string Url, string RequestBody, int StatusCode, string ResponseBody)
{
    public JsonNode? RequestJson
    {
        get
        {
            try { return string.IsNullOrWhiteSpace(RequestBody) ? null : JsonNode.Parse(RequestBody); }
            catch (JsonException) { return null; }
        }
    }
}

/// <summary>
/// Minimal FHIR R4 endpoint (JSON) so a LIS can push resources to the emulator:
/// POST/PUT resources, POST transaction/batch bundles to the base, POST $process-message,
/// GET metadata, GET [type]/[id] and GET [type] (search returns everything stored of that type).
/// </summary>
public sealed class FhirServer
{
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, JsonObject>> _store = new();
    private int _nextId;

    public event Action<LogEntry>? Log;
    public event Action<FhirExchange>? RequestHandled;

    public FhirServerResponseMode ResponseMode { get; set; }
    public string? Prefix { get; private set; }
    public bool IsRunning => _listener?.IsListening == true;

    public void Start(string host, int port, string path)
    {
        if (IsRunning) return;
        path = path.Trim('/');
        Prefix = $"http://{(string.IsNullOrWhiteSpace(host) ? "localhost" : host)}:{port}/{(path.Length > 0 ? path + "/" : "")}";
        _listener = new HttpListener();
        _listener.Prefixes.Add(Prefix);
        try { _listener.Start(); }
        catch (HttpListenerException ex)
        {
            _listener = null;
            throw new InvalidOperationException(ex.ErrorCode == 5
                ? $"Access denied for {Prefix}. Use host 'localhost', run as administrator, or register the URL once with:\n" +
                  $"netsh http add urlacl url={Prefix} user=%USERNAME%"
                : $"Cannot listen on {Prefix}: {ex.Message}");
        }
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => LoopAsync(_listener, _cts.Token));
        Write(LogDirection.Info, $"FHIR server listening on {Prefix}");
    }

    public async Task StopAsync()
    {
        if (_listener == null) return;
        _cts?.Cancel();
        try { _listener.Stop(); _listener.Close(); } catch { }
        if (_loop != null) { try { await _loop; } catch { } }
        _listener = null;
        _cts = null;
        Write(LogDirection.Info, "FHIR server stopped");
    }

    private async Task LoopAsync(HttpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await listener.GetContextAsync(); }
            catch when (ct.IsCancellationRequested || !listener.IsListening) { break; }
            catch (Exception ex) { Write(LogDirection.Error, "FHIR server: " + ex.Message); continue; }
            _ = Task.Run(() => HandleAsync(ctx));
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        string body;
        using (var reader = new StreamReader(req.InputStream, req.ContentEncoding ?? Encoding.UTF8))
            body = await reader.ReadToEndAsync();

        var relative = RelativePath(req.Url!);
        Write(LogDirection.Rx, $"{req.HttpMethod} {req.Url!.PathAndQuery}" + (body.Length > 0 ? $"  ({body.Length} bytes)" : ""));

        int status;
        JsonNode? response;
        string? location = null;
        try
        {
            (status, response, location) = Process(req.HttpMethod, relative, req.Url!.Query, body);
        }
        catch (Exception ex)
        {
            status = 500;
            response = FhirBuilder.OperationOutcome("fatal", "exception", ex.Message);
        }

        var responseText = response?.ToJsonString(FhirBuilder.JsonOptions) ?? "";
        try
        {
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = "application/fhir+json; charset=utf-8";
            if (location != null) ctx.Response.Headers["Location"] = location;
            var bytes = Encoding.UTF8.GetBytes(responseText);
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        }
        catch (Exception ex) { Write(LogDirection.Warn, "Could not send FHIR response: " + ex.Message); }

        Write(status < 300 ? LogDirection.Tx : LogDirection.Warn, $"{status} {(HttpStatusCode)status}" + (location != null ? $"  Location: {location}" : ""));
        RequestHandled?.Invoke(new FhirExchange(DateTime.Now, req.HttpMethod, req.Url!.PathAndQuery, FhirClient.Pretty(body), status, responseText));
    }

    private string RelativePath(Uri url)
    {
        // "/fhir" and "/fhir/" both mean the base URL
        var basePath = new Uri(Prefix!.Replace("+", "localhost").Replace("*", "localhost")).AbsolutePath.TrimEnd('/');
        var p = url.AbsolutePath;
        if (p.StartsWith(basePath, StringComparison.OrdinalIgnoreCase)) p = p[basePath.Length..];
        return Uri.UnescapeDataString(p.Trim('/'));
    }

    /// <summary>Core request handling; public for testing.</summary>
    public (int Status, JsonNode? Body, string? Location) Process(string method, string path, string query, string body)
    {
        method = method.ToUpperInvariant();
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (method == "GET" && parts is ["metadata"]) return (200, CapabilityStatement(), null);

        if (ResponseMode == FhirServerResponseMode.RejectBadRequest && method is "POST" or "PUT")
            return (400, FhirBuilder.OperationOutcome("error", "invalid", "Rejected by emulator (simulated validation error)"), null);
        if (ResponseMode == FhirServerResponseMode.ServerError)
            return (500, FhirBuilder.OperationOutcome("fatal", "exception", "Simulated server error"), null);

        JsonObject? resource = null;
        if (method is "POST" or "PUT")
        {
            try { resource = JsonNode.Parse(body) as JsonObject; }
            catch (JsonException ex) { return (400, FhirBuilder.OperationOutcome("error", "structure", "Invalid JSON: " + ex.Message), null); }
            if (resource == null || resource["resourceType"] is null)
                return (400, FhirBuilder.OperationOutcome("error", "structure", "Body is not a FHIR resource (missing resourceType)"), null);
        }

        switch (method, parts.Length)
        {
            case ("POST", 0):
                return ProcessBundle(resource!);
            case ("POST", 1) when parts[0] == "$process-message":
                return ProcessMessage(resource!);
            case ("POST", 1):
            {
                var id = Store(parts[0], resource!, null);
                return (201, resource, $"{parts[0]}/{id}/_history/1");
            }
            case ("PUT", 2):
            {
                bool existed = _store.TryGetValue(parts[0], out var t) && t.ContainsKey(parts[1]);
                Store(parts[0], resource!, parts[1]);
                return (existed ? 200 : 201, resource, $"{parts[0]}/{parts[1]}/_history/1");
            }
            case ("GET", 2):
                return _store.TryGetValue(parts[0], out var type) && type.TryGetValue(parts[1], out var found)
                    ? (200, found, null)
                    : (404, FhirBuilder.OperationOutcome("error", "not-found", $"{parts[0]}/{parts[1]} not found"), null);
            case ("GET", 1):
                return (200, SearchSet(parts[0]), null);
            default:
                return (405, FhirBuilder.OperationOutcome("error", "not-supported", $"{method} /{path} is not supported by the emulator"), null);
        }
    }

    private string Store(string type, JsonObject resource, string? id)
    {
        id ??= resource["id"]?.GetValue<string>() is { Length: > 0 } existing && type != "Bundle"
            ? existing
            : Interlocked.Increment(ref _nextId).ToString(CultureInfo.InvariantCulture);
        resource["id"] = id;
        resource["meta"] = new JsonObject { ["versionId"] = "1", ["lastUpdated"] = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture) };
        _store.GetOrAdd(type, _ => new())[id] = resource;
        return id;
    }

    private (int, JsonNode?, string?) ProcessBundle(JsonObject bundle)
    {
        var bundleType = bundle["type"]?.GetValue<string>();
        if (bundle["resourceType"]?.GetValue<string>() != "Bundle" || bundleType is not ("transaction" or "batch"))
            return (400, FhirBuilder.OperationOutcome("error", "invalid", "POST to the base URL requires a transaction or batch Bundle"), null);

        var responseEntries = new JsonArray();
        foreach (var e in bundle["entry"]?.AsArray() ?? new JsonArray())
        {
            var res = e?["resource"]?.DeepClone() as JsonObject;
            var type = res?["resourceType"]?.GetValue<string>() ?? "Unknown";
            if (res == null) continue;
            var id = Store(type, res, null);
            responseEntries.Add(new JsonObject
            {
                ["response"] = new JsonObject { ["status"] = "201 Created", ["location"] = $"{type}/{id}/_history/1" },
            });
        }
        return (200, new JsonObject
        {
            ["resourceType"] = "Bundle",
            ["type"] = bundleType + "-response",
            ["entry"] = responseEntries,
        }, null);
    }

    private (int, JsonNode?, string?) ProcessMessage(JsonObject bundle)
    {
        if (bundle["type"]?.GetValue<string>() != "message")
            return (400, FhirBuilder.OperationOutcome("error", "invalid", "$process-message requires a Bundle of type 'message'"), null);
        foreach (var e in bundle["entry"]?.AsArray() ?? new JsonArray())
            if (e?["resource"]?.DeepClone() is JsonObject res)
                Store(res["resourceType"]?.GetValue<string>() ?? "Unknown", res, null);

        var incomingId = bundle["identifier"]?["value"]?.GetValue<string>() ?? "";
        var header = new JsonObject
        {
            ["resourceType"] = "MessageHeader",
            ["eventCoding"] = bundle["entry"]?[0]?["resource"]?["eventCoding"]?.DeepClone(),
            ["source"] = new JsonObject { ["name"] = "HL7/FHIR emulator", ["endpoint"] = Prefix },
            ["response"] = new JsonObject { ["identifier"] = incomingId, ["code"] = "ok" },
        };
        return (200, new JsonObject
        {
            ["resourceType"] = "Bundle",
            ["type"] = "message",
            ["timestamp"] = DateTime.Now.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture),
            ["entry"] = new JsonArray(new JsonObject { ["fullUrl"] = "urn:uuid:" + Guid.NewGuid(), ["resource"] = header }),
        }, null);
    }

    private JsonObject SearchSet(string type)
    {
        var items = _store.TryGetValue(type, out var t) ? t.Values.ToList() : [];
        return new JsonObject
        {
            ["resourceType"] = "Bundle",
            ["type"] = "searchset",
            ["total"] = items.Count,
            ["entry"] = new JsonArray(items.Select(r => (JsonNode)new JsonObject
            {
                ["fullUrl"] = $"{Prefix}{type}/{r["id"]}",
                ["resource"] = r.DeepClone(),
            }).ToArray()),
        };
    }

    private JsonObject CapabilityStatement() => new()
    {
        ["resourceType"] = "CapabilityStatement",
        ["status"] = "active",
        ["date"] = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        ["kind"] = "instance",
        ["software"] = new JsonObject { ["name"] = "HL7/FHIR emulator" },
        ["fhirVersion"] = "4.0.1",
        ["format"] = new JsonArray("application/fhir+json"),
        ["rest"] = new JsonArray(new JsonObject
        {
            ["mode"] = "server",
            ["interaction"] = new JsonArray(new JsonObject { ["code"] = "transaction" }, new JsonObject { ["code"] = "batch" }),
            ["operation"] = new JsonArray(new JsonObject { ["name"] = "process-message", ["definition"] = "http://hl7.org/fhir/OperationDefinition/MessageHeader-process-message" }),
        }),
    };

    private void Write(LogDirection d, string text) => Log?.Invoke(new LogEntry(DateTime.Now, d, text));
}
