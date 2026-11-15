using System.Text.Json.Nodes;
using Hl7Emulator.Core.Hl7v2;

namespace Hl7Emulator.Core;

/// <summary>An order found in an incoming HL7 v2 message or FHIR payload.</summary>
public sealed record IncomingOrder(string SpecimenId, string PatientId, string LastName, string FirstName, string BirthDate, string Sex, string TestCode);

public static class IncomingOrders
{
    /// <summary>Orders from ORM/OML/RSP/OUL (first PID applies to all orders in the message).</summary>
    public static List<IncomingOrder> FromHl7(Hl7Message m)
    {
        var result = new List<IncomingOrder>();
        var pid = m.All("PID").FirstOrDefault();
        string Pid(int f, int c = 1) => pid == null ? "" : m.ComponentOf(pid, f, c);
        var spm = m.All("SPM").ToList();
        int i = 0;
        foreach (var obr in m.All("OBR"))
        {
            var spec = i < spm.Count ? m.ComponentOf(spm[i], 2, 1) : "";
            if (spec.Length == 0) spec = m.ComponentOf(obr, 3, 1);
            if (spec.Length == 0) spec = m.ComponentOf(obr, 2, 1);
            i++;
            if (spec.Length == 0) continue;
            result.Add(new IncomingOrder(spec, Pid(3), Pid(5, 1), Pid(5, 2), Pid(7), Pid(8), m.ComponentOf(obr, 4, 1)));
        }
        return result;
    }

    /// <summary>Orders (ServiceRequests) from a FHIR resource or Bundle.</summary>
    public static List<IncomingOrder> FromFhir(JsonNode? root)
    {
        var result = new List<IncomingOrder>();
        if (root is not JsonObject obj) return result;

        // Collect resources with the keys they can be referenced by.
        var resources = new List<JsonObject>();
        var byRef = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        void Add(JsonObject r, string? fullUrl)
        {
            resources.Add(r);
            if (fullUrl != null) byRef[fullUrl] = r;
            var type = Str(r["resourceType"]);
            var id = Str(r["id"]);
            if (type.Length > 0 && id.Length > 0)
            {
                byRef[$"{type}/{id}"] = r;
                byRef[$"#{id}"] = r;
            }
            foreach (var c in r["contained"]?.AsArray() ?? new JsonArray())
                if (c is JsonObject co) Add(co, null);
        }

        if (Str(obj["resourceType"]) == "Bundle")
        {
            foreach (var e in obj["entry"]?.AsArray() ?? new JsonArray())
                if (e?["resource"] is JsonObject r) Add(r, Str(e["fullUrl"]) is { Length: > 0 } u ? u : null);
        }
        else Add(obj, null);

        JsonObject? Resolve(JsonNode? reference)
        {
            var r = Str(reference?["reference"]);
            if (r.Length == 0) return null;
            if (byRef.TryGetValue(r, out var hit)) return hit;
            // absolute URL ending in Type/id
            var tail = string.Join('/', r.Split('/').TakeLast(2));
            return byRef.GetValueOrDefault(tail);
        }

        foreach (var sr in resources.Where(r => Str(r["resourceType"]) == "ServiceRequest"))
        {
            var specimen = Resolve(sr["specimen"]?[0]);
            var spec = Str(specimen?["accessionIdentifier"]?["value"]);
            if (spec.Length == 0) spec = Str(specimen?["identifier"]?[0]?["value"]);
            if (spec.Length == 0) spec = Str(sr["identifier"]?[0]?["value"]);
            if (spec.Length == 0) spec = Str(sr["id"]);
            if (spec.Length == 0) continue;

            var patient = Resolve(sr["subject"]);
            var name = patient?["name"]?[0];
            var dob = Str(patient?["birthDate"]).Replace("-", "");
            var sex = Str(patient?["gender"]) switch { "female" => "F", "male" => "M", "other" => "O", "unknown" => "U", _ => "" };
            var code = Str(sr["code"]?["coding"]?[0]?["code"]);
            result.Add(new IncomingOrder(spec, Str(patient?["identifier"]?[0]?["value"]), Str(name?["family"]), Str(name?["given"]?[0]), dob, sex, code));
        }
        return result;
    }

    private static string Str(JsonNode? n)
    {
        try { return n?.GetValue<string>() ?? ""; }
        catch (InvalidOperationException) { return ""; }
    }
}
