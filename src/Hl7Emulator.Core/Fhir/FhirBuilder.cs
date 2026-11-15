using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ViperAstmEmulator.Core.Viper;

namespace Hl7Emulator.Core.Fhir;

public enum FhirRequestKind
{
    ResultTransaction,
    ResultMessage,
    OrderTransaction,
    DiagnosticReportOnly,
    PatientCreate,
    SearchOrders,
    Metadata,
}

public sealed record FhirRequestKindInfo(FhirRequestKind Kind, string Method, string Path, string Title, string Description)
{
    public string Display => $"{Title}  -  {Description}";
}

/// <summary>A FHIR HTTP request prepared for sending.</summary>
public sealed record FhirRequest(string Method, string Path, string? Body);

/// <summary>Builds FHIR R4 resources and bundles (JSON) from the shared specimen model.</summary>
public sealed class FhirBuilder(Hl7Settings s)
{
    public static readonly IReadOnlyList<FhirRequestKindInfo> Kinds =
    [
        new(FhirRequestKind.ResultTransaction, "POST", "", "Results (transaction)",
            "Bundle with Patient, Specimen, ServiceRequest, Observations and DiagnosticReport, POSTed to the base URL"),
        new(FhirRequestKind.ResultMessage, "POST", "$process-message", "Results (FHIR message)",
            "Message Bundle with MessageHeader + DiagnosticReport + Observations, POSTed to $process-message"),
        new(FhirRequestKind.OrderTransaction, "POST", "", "Order (transaction)",
            "Bundle with Patient, Specimen and ServiceRequest (a new lab order)"),
        new(FhirRequestKind.DiagnosticReportOnly, "POST", "DiagnosticReport", "DiagnosticReport only",
            "Single DiagnosticReport with the results contained inside it"),
        new(FhirRequestKind.PatientCreate, "POST", "Patient", "Patient",
            "Create a single Patient resource"),
        new(FhirRequestKind.SearchOrders, "GET", "ServiceRequest?status=active", "Search open orders",
            "GET active ServiceRequests from the LIS (orders are added to the specimen list)"),
        new(FhirRequestKind.Metadata, "GET", "metadata", "Capability statement",
            "GET /metadata - quick check that the FHIR server is reachable"),
    ];

    public static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private const string Snomed = "http://snomed.info/sct";
    private const string Interpretation = "http://terminology.hl7.org/CodeSystem/v3-ObservationInterpretation";

    private static string Iso(DateTime t) => t.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);

    private static string? IsoDate(string yyyymmdd) =>
        DateTime.TryParseExact(yyyymmdd, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null;

    private static string Gender(string sex) => sex.ToUpperInvariant() switch
    {
        "F" => "female",
        "M" => "male",
        "O" => "other",
        _ => "unknown",
    };

    private JsonObject Coding(string system, string code, string? display = null)
    {
        var c = new JsonObject { ["system"] = system, ["code"] = code };
        if (!string.IsNullOrEmpty(display)) c["display"] = display;
        return c;
    }

    private JsonObject Concept(string system, string code, string? display = null, string? text = null)
    {
        var o = new JsonObject { ["coding"] = new JsonArray(Coding(system, code, display)) };
        if (text != null) o["text"] = text;
        return o;
    }

    private static JsonObject Ref(string reference, string? display = null)
    {
        var r = new JsonObject { ["reference"] = reference };
        if (display != null) r["display"] = display;
        return r;
    }

    private static JsonObject Identifier(string system, string value) => new() { ["system"] = system, ["value"] = value };

    public JsonObject Patient(Sample p)
    {
        var o = new JsonObject
        {
            ["resourceType"] = "Patient",
            ["identifier"] = new JsonArray(Identifier(s.PatientIdSystem, p.PatientId)),
            ["name"] = new JsonArray(new JsonObject
            {
                ["family"] = p.LastName,
                ["given"] = new JsonArray(p.FirstName),
            }),
            ["gender"] = Gender(p.Sex),
        };
        var dob = IsoDate(p.BirthDate);
        if (dob != null) o["birthDate"] = dob;
        return o;
    }

    public JsonObject Specimen(Sample p, string patientRef, DateTime ts)
    {
        var desc = SpecimenTypes.Find(s.SpecimenType)?.Description ?? s.SpecimenType;
        return new JsonObject
        {
            ["resourceType"] = "Specimen",
            ["identifier"] = new JsonArray(Identifier(s.SpecimenIdSystem, p.SpecimenId)),
            ["accessionIdentifier"] = Identifier(s.SpecimenIdSystem, p.SpecimenId),
            ["status"] = "available",
            ["type"] = Concept(s.LocalCodeSystem, s.SpecimenType, desc),
            ["subject"] = Ref(patientRef),
            ["receivedTime"] = Iso(ts.AddHours(-20)),
            ["collection"] = new JsonObject { ["collectedDateTime"] = Iso(ts.AddHours(-24)) },
        };
    }

    public JsonObject ServiceRequest(Sample p, string patientRef, string specimenRef, DateTime ts, bool completed)
    {
        return new JsonObject
        {
            ["resourceType"] = "ServiceRequest",
            ["identifier"] = new JsonArray(Identifier(s.OrderIdSystem, p.SpecimenId)),
            ["status"] = completed ? "completed" : "active",
            ["intent"] = "order",
            ["category"] = new JsonArray(Concept(Snomed, "108252007", "Laboratory procedure")),
            ["code"] = Concept(s.LocalCodeSystem, s.AssayCode, s.AssayName),
            ["subject"] = Ref(patientRef),
            ["specimen"] = new JsonArray(Ref(specimenRef)),
            ["authoredOn"] = Iso(ts.AddHours(-24)),
        };
    }

    private (string sctCode, string sctDisplay, string interp)? ResultCoding(string result) => result switch
    {
        ResultValues.Positive => ("10828004", "Positive", "POS"),
        ResultValues.Negative => ("260385009", "Negative", "NEG"),
        ResultValues.Indeterminate => ("82334004", "Indeterminate", "IND"),
        _ => null,
    };

    private JsonObject ValueConcept(string result)
    {
        var rc = ResultCoding(result);
        return rc == null
            ? new JsonObject { ["text"] = result == ResultValues.Invalid ? "Invalid" : result }
            : Concept(Snomed, rc.Value.sctCode, rc.Value.sctDisplay, rc.Value.sctDisplay);
    }

    public JsonObject Observation(TargetResult r, string patientRef, string specimenRef, string? orderRef, DateTime ts)
    {
        var o = new JsonObject
        {
            ["resourceType"] = "Observation",
            ["status"] = "final",
            ["category"] = new JsonArray(Concept("http://terminology.hl7.org/CodeSystem/observation-category", "laboratory", "Laboratory")),
            ["code"] = Concept(s.LocalCodeSystem, r.Code, r.Name, r.Name),
            ["subject"] = Ref(patientRef),
            ["effectiveDateTime"] = Iso(ts.AddHours(-24)),
            ["issued"] = Iso(ts),
            ["valueCodeableConcept"] = ValueConcept(r.Result),
            ["specimen"] = Ref(specimenRef),
            ["device"] = new JsonObject { ["display"] = s.InstrumentId },
        };
        if (orderRef != null) o["basedOn"] = new JsonArray(Ref(orderRef));
        var rc = ResultCoding(r.Result);
        if (rc != null) o["interpretation"] = new JsonArray(Concept(Interpretation, rc.Value.interp));
        if (s.IncludeCt && double.TryParse(r.Ct, NumberStyles.Float, CultureInfo.InvariantCulture, out var ct))
        {
            o["component"] = new JsonArray(new JsonObject
            {
                ["code"] = Concept(s.LocalCodeSystem, r.Code + "_CT", r.Name + " Ct"),
                ["valueQuantity"] = new JsonObject
                {
                    ["value"] = ct,
                    ["unit"] = "cycles",
                    ["system"] = "http://unitsofmeasure.org",
                    ["code"] = "{cycles}",
                },
            });
        }
        return o;
    }

    public JsonObject OverallObservation(Sample p, string patientRef, string specimenRef, string? orderRef, DateTime ts)
    {
        var o = Observation(new TargetResult { Code = s.AssayCode, Name = s.AssayName + " interpretation", Result = p.OverallResult }, patientRef, specimenRef, orderRef, ts);
        if (!string.IsNullOrWhiteSpace(p.Comment)) o["note"] = new JsonArray(new JsonObject { ["text"] = p.Comment });
        return o;
    }

    public JsonObject DiagnosticReport(Sample p, string patientRef, string specimenRef, string? orderRef, IEnumerable<string> resultRefs, DateTime ts)
    {
        var o = new JsonObject
        {
            ["resourceType"] = "DiagnosticReport",
            ["identifier"] = new JsonArray(Identifier(s.OrderIdSystem, p.SpecimenId)),
            ["status"] = "final",
            ["category"] = new JsonArray(Concept("http://terminology.hl7.org/CodeSystem/v2-0074", "LAB", "Laboratory")),
            ["code"] = Concept(s.LocalCodeSystem, s.AssayCode, s.AssayName, s.AssayName),
            ["subject"] = Ref(patientRef),
            ["effectiveDateTime"] = Iso(ts.AddHours(-24)),
            ["issued"] = Iso(ts),
            ["performer"] = new JsonArray(new JsonObject { ["display"] = s.SendingFacility }),
            ["specimen"] = new JsonArray(Ref(specimenRef)),
            ["result"] = new JsonArray(resultRefs.Select(r => (JsonNode)Ref(r)).ToArray()),
            ["conclusion"] = $"{s.AssayName}: {p.Summary}",
        };
        if (orderRef != null) o["basedOn"] = new JsonArray(Ref(orderRef));
        return o;
    }

    private IEnumerable<TargetResult> ReportedResults(Sample p) => p.Results.Where(r => !r.IsControl || s.IncludeControlResult);

    private static string NewUrn() => "urn:uuid:" + Guid.NewGuid();

    private static JsonObject Entry(string fullUrl, JsonObject resource, string? requestMethod = null)
    {
        var e = new JsonObject { ["fullUrl"] = fullUrl, ["resource"] = resource };
        if (requestMethod != null)
            e["request"] = new JsonObject { ["method"] = requestMethod, ["url"] = resource["resourceType"]!.GetValue<string>() };
        return e;
    }

    /// <summary>Patient, Specimen, ServiceRequest and (optionally) results, as (fullUrl, resource) pairs with urn:uuid references.</summary>
    private List<(string Url, JsonObject Res)> ResourceSet(Sample p, DateTime ts, bool withResults)
    {
        string pat = NewUrn(), spec = NewUrn(), order = NewUrn();
        var list = new List<(string, JsonObject)>
        {
            (pat, Patient(p)),
            (spec, Specimen(p, pat, ts)),
            (order, ServiceRequest(p, pat, spec, ts, completed: withResults)),
        };
        if (withResults)
        {
            var obsRefs = new List<string>();
            foreach (var r in ReportedResults(p))
            {
                var u = NewUrn();
                obsRefs.Add(u);
                list.Add((u, Observation(r, pat, spec, order, ts)));
            }
            if (s.IncludeOverallResult)
            {
                var u = NewUrn();
                obsRefs.Add(u);
                list.Add((u, OverallObservation(p, pat, spec, order, ts)));
            }
            list.Add((NewUrn(), DiagnosticReport(p, pat, spec, order, obsRefs, ts)));
        }
        return list;
    }

    public JsonObject TransactionBundle(Sample p, bool withResults, DateTime? timestamp = null)
    {
        var ts = timestamp ?? DateTime.Now;
        return new JsonObject
        {
            ["resourceType"] = "Bundle",
            ["type"] = "transaction",
            ["timestamp"] = Iso(ts),
            ["entry"] = new JsonArray(ResourceSet(p, ts, withResults).Select(x => (JsonNode)Entry(x.Url, x.Res, "POST")).ToArray()),
        };
    }

    public JsonObject MessageBundle(Sample p, DateTime? timestamp = null)
    {
        var ts = timestamp ?? DateTime.Now;
        var set = ResourceSet(p, ts, withResults: true);
        var report = set.Last(); // DiagnosticReport
        var header = new JsonObject
        {
            ["resourceType"] = "MessageHeader",
            ["eventCoding"] = Coding(s.MessageEventSystem, "diagnosticreport-provide", "Provide diagnostic report"),
            ["destination"] = new JsonArray(new JsonObject { ["name"] = s.ReceivingApplication, ["endpoint"] = s.FhirBaseUrl }),
            ["source"] = new JsonObject { ["name"] = s.SendingApplication, ["software"] = "HL7/FHIR emulator", ["endpoint"] = "urn:" + s.SendingApplication },
            ["focus"] = new JsonArray(Ref(report.Url)),
        };
        var entries = new List<JsonNode> { Entry(NewUrn(), header) };
        entries.Add(Entry(report.Url, report.Res));
        entries.AddRange(set.Take(set.Count - 1).Select(x => (JsonNode)Entry(x.Url, x.Res)));
        return new JsonObject
        {
            ["resourceType"] = "Bundle",
            ["identifier"] = new JsonObject { ["system"] = "urn:ietf:rfc:3986", ["value"] = NewUrn() },
            ["type"] = "message",
            ["timestamp"] = Iso(ts),
            ["entry"] = new JsonArray(entries.ToArray()),
        };
    }

    /// <summary>DiagnosticReport with Patient, Specimen and Observations as contained resources.</summary>
    public JsonObject StandaloneReport(Sample p, DateTime? timestamp = null)
    {
        var ts = timestamp ?? DateTime.Now;
        var contained = new JsonArray();
        var pat = Patient(p); pat["id"] = "pat";
        var spec = Specimen(p, "#pat", ts); spec["id"] = "spec";
        contained.Add(pat);
        contained.Add(spec);
        var refs = new List<string>();
        int i = 1;
        foreach (var r in ReportedResults(p))
        {
            var o = Observation(r, "#pat", "#spec", null, ts);
            o["id"] = $"obs{i}";
            refs.Add($"#obs{i++}");
            contained.Add(o);
        }
        var report = DiagnosticReport(p, "#pat", "#spec", null, refs, ts);
        report["contained"] = contained;
        // contained resources first for readability
        var ordered = new JsonObject { ["resourceType"] = "DiagnosticReport" };
        foreach (var kv in report.ToList())
        {
            if (kv.Key == "resourceType") continue;
            report.Remove(kv.Key);
            ordered[kv.Key] = kv.Value;
        }
        return ordered;
    }

    public FhirRequest Build(FhirRequestKind kind, Sample? p, DateTime? timestamp = null)
    {
        var info = Kinds.First(k => k.Kind == kind);
        if (kind is FhirRequestKind.SearchOrders or FhirRequestKind.Metadata)
            return new FhirRequest(info.Method, info.Path, null);
        if (p == null) throw new InvalidOperationException("Select a specimen first.");
        JsonObject body = kind switch
        {
            FhirRequestKind.ResultTransaction => TransactionBundle(p, withResults: true, timestamp),
            FhirRequestKind.OrderTransaction => TransactionBundle(p, withResults: false, timestamp),
            FhirRequestKind.ResultMessage => MessageBundle(p, timestamp),
            FhirRequestKind.DiagnosticReportOnly => StandaloneReport(p, timestamp),
            FhirRequestKind.PatientCreate => Patient(p),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        return new FhirRequest(info.Method, info.Path, body.ToJsonString(JsonOptions));
    }

    public static JsonObject OperationOutcome(string severity, string code, string text) => new()
    {
        ["resourceType"] = "OperationOutcome",
        ["issue"] = new JsonArray(new JsonObject { ["severity"] = severity, ["code"] = code, ["diagnostics"] = text }),
    };
}
