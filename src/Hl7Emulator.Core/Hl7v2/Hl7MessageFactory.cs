using System.Globalization;
using ViperAstmEmulator.Core.Viper;

namespace Hl7Emulator.Core.Hl7v2;

public enum Hl7MessageKind
{
    OruR01,
    OulR22,
    OrmO01,
    OmlO21,
    AdtA01,
    AdtA04,
    AdtA08,
    QbpQ11,
}

public sealed record Hl7MessageKindInfo(Hl7MessageKind Kind, string Code, string Structure, string Description)
{
    public string Display => $"{Code}  -  {Description}";
}

/// <summary>Builds HL7 v2 messages (2.3.1 / 2.4 / 2.5.1) from the shared specimen model.</summary>
public sealed class Hl7MessageFactory(Hl7Settings s)
{
    public static readonly IReadOnlyList<Hl7MessageKindInfo> Kinds =
    [
        new(Hl7MessageKind.OruR01, "ORU^R01", "ORU_R01", "Observation results (lab results to LIS) - most common"),
        new(Hl7MessageKind.OulR22, "OUL^R22", "OUL_R22", "Specimen-oriented results (IHE LAW analyzer results, v2.5+)"),
        new(Hl7MessageKind.OrmO01, "ORM^O01", "ORM_O01", "General order (new order for the specimen)"),
        new(Hl7MessageKind.OmlO21, "OML^O21", "OML_O21", "Laboratory order (v2.5 style order with specimen)"),
        new(Hl7MessageKind.AdtA01, "ADT^A01", "ADT_A01", "Admit / visit notification (patient admitted)"),
        new(Hl7MessageKind.AdtA04, "ADT^A04", "ADT_A01", "Register a patient (outpatient registration)"),
        new(Hl7MessageKind.AdtA08, "ADT^A08", "ADT_A01", "Update patient information"),
        new(Hl7MessageKind.QbpQ11, "QBP^Q11", "QBP_Q11", "Query the LIS for orders of a specimen (IHE LAW worklist query)"),
    ];

    public const string DateFormat = "yyyyMMddHHmmss";
    private static int _counter;

    private static string Ts(DateTime t) => t.ToString(DateFormat, CultureInfo.InvariantCulture);
    private static string E(string? v) => Hl7Message.Escape(v);

    /// <summary>Joins escaped components with '^', dropping trailing empty ones.</summary>
    public static string Comp(params string?[] parts)
    {
        int last = parts.Length - 1;
        while (last >= 0 && string.IsNullOrEmpty(parts[last])) last--;
        return string.Join("^", parts.Take(last + 1).Select(E));
    }

    /// <summary>Segment from fields indexed by HL7 field number (index 0 = segment id). Trailing empty fields are dropped.</summary>
    private static string Seg(string id, params string[] fields)
    {
        int last = fields.Length - 1;
        while (last >= 0 && fields[last].Length == 0) last--;
        return last < 0 ? id : id + "|" + string.Join("|", fields.Take(last + 1));
    }

    private static string[] Fields(int count)
    {
        var f = new string[count];
        Array.Fill(f, "");
        return f;
    }

    public static string NewControlId() =>
        $"{DateTime.Now:yyyyMMddHHmmssfff}{Interlocked.Increment(ref _counter) % 1000:000}";

    public string Msh(string code, string trigger, string structure, DateTime ts, string? controlId = null)
    {
        // fields[0] = MSH-3 ... fields[9] = MSH-12
        var fields = new List<string>
        {
            E(s.SendingApplication), E(s.SendingFacility), E(s.ReceivingApplication), E(s.ReceivingFacility),
            Ts(ts), "", $"{code}^{trigger}^{structure}", controlId ?? NewControlId(), E(s.ProcessingId), E(s.Hl7Version),
        };
        if (s.IncludeCharsetInMsh)
        {
            while (fields.Count < 15) fields.Add("");          // up to MSH-17
            fields.Add(s.CharacterSet.Equals("ISO-8859-1", StringComparison.OrdinalIgnoreCase) ? "8859/1" : "UNICODE UTF-8"); // MSH-18
        }
        return @"MSH|^~\&|" + string.Join("|", fields);
    }

    public string Pid(Sample p)
    {
        var f = Fields(9);
        f[0] = "1";
        f[2] = Comp(p.PatientId, "", "", s.SendingFacility, "MR");   // PID-3
        f[4] = Comp(p.LastName, p.FirstName);                        // PID-5
        f[6] = E(p.BirthDate);                                       // PID-7
        f[7] = E(p.Sex);                                             // PID-8
        return Seg("PID", f);
    }

    public static string Pv1() => "PV1|1|O";

    private string Orc(Sample p, string control, DateTime ts)
    {
        var f = Fields(9);
        f[0] = control;                  // ORC-1: NW new, RE results
        f[1] = E(p.SpecimenId);          // ORC-2 placer order number
        f[2] = E(p.SpecimenId);          // ORC-3 filler order number
        f[4] = control == "NW" ? "SC" : "CM"; // ORC-5 order status
        f[8] = Ts(ts);                   // ORC-9 transaction date/time
        return Seg("ORC", f);
    }

    private string Obr(Sample p, DateTime ts, bool isResult)
    {
        var f = Fields(25);
        f[0] = "1";
        f[1] = E(p.SpecimenId);                                  // OBR-2 placer order number
        f[2] = E(p.SpecimenId);                                  // OBR-3 filler order number
        f[3] = Comp(s.AssayCode, s.AssayName, "L");              // OBR-4 universal service ID
        f[6] = Ts(ts.AddHours(-24));                             // OBR-7 observation (collection) date/time
        if (!s.IsV25OrLater) f[14] = Comp(s.SpecimenType);       // OBR-15 specimen source (pre-2.5)
        if (isResult)
        {
            f[13] = Ts(ts.AddHours(-20));                        // OBR-14 specimen received
            f[21] = Ts(ts);                                      // OBR-22 results reported
            f[24] = "F";                                         // OBR-25 result status: final
        }
        return Seg("OBR", f);
    }

    private string Spm(Sample p, DateTime ts)
    {
        var desc = SpecimenTypes.Find(s.SpecimenType)?.Description ?? s.SpecimenType;
        var f = Fields(18);
        f[0] = "1";
        f[1] = Comp(p.SpecimenId);                               // SPM-2 specimen ID
        f[3] = Comp(s.SpecimenType, desc, "L");                  // SPM-4 specimen type
        f[16] = Ts(ts.AddHours(-24));                            // SPM-17 collection date/time
        f[17] = Ts(ts.AddHours(-20));                            // SPM-18 received date/time
        return Seg("SPM", f);
    }

    private static string ResultText(string r) => r switch
    {
        ResultValues.Positive => "Positive",
        ResultValues.Negative => "Negative",
        ResultValues.Indeterminate => "Indeterminate",
        ResultValues.Invalid => "Invalid",
        _ => r,
    };

    private static string AbnormalFlag(string r) => r switch
    {
        ResultValues.Positive => "A",
        ResultValues.Negative => "N",
        _ => "",
    };

    private string Obx(int seq, string valueType, string code, string name, string value, string units, string flag, DateTime ts)
    {
        var f = Fields(19);
        f[0] = seq.ToString(CultureInfo.InvariantCulture);
        f[1] = valueType;                                        // OBX-2
        f[2] = Comp(code, name, "L");                            // OBX-3
        f[4] = value;                                            // OBX-5 (already encoded)
        f[5] = units;                                            // OBX-6
        f[7] = flag;                                             // OBX-8 abnormal flag
        f[10] = "F";                                             // OBX-11 status
        f[13] = Ts(ts);                                          // OBX-14 observation date/time
        f[17] = Comp(s.InstrumentId);                            // OBX-18 equipment instance
        if (s.IsV25OrLater) f[18] = Ts(ts);                      // OBX-19 analysis date/time
        return Seg("OBX", f);
    }

    private List<string> ResultObxs(Sample p, DateTime ts)
    {
        var list = new List<string>();
        var coded = s.IsV25OrLater ? "CWE" : "CE";
        int seq = 1;
        foreach (var r in p.Results)
        {
            if (r.IsControl && !s.IncludeControlResult) continue;
            // A positive internal control is the normal outcome; a failed control is abnormal.
            var flag = r.IsControl ? (r.Result == ResultValues.Positive ? "N" : "A") : AbnormalFlag(r.Result);
            list.Add(Obx(seq++, coded, r.Code, r.Name, Comp(r.Result, ResultText(r.Result), "L"), "", flag, ts));
            if (s.IncludeCt && !string.IsNullOrWhiteSpace(r.Ct))
                list.Add(Obx(seq++, "NM", r.Code + "_CT", r.Name + " Ct", E(r.Ct), "{cycles}", "", ts));
        }
        if (s.IncludeOverallResult)
            list.Add(Obx(seq, coded, s.AssayCode, s.AssayName + " interpretation",
                Comp(p.OverallResult, ResultText(p.OverallResult), "L"), "", AbnormalFlag(p.OverallResult), ts));
        return list;
    }

    private static string? Nte(Sample p) => string.IsNullOrWhiteSpace(p.Comment) ? null : $"NTE|1|L|{E(p.Comment)}";

    public string Build(Hl7MessageKind kind, Sample p, DateTime? timestamp = null)
    {
        var ts = timestamp ?? DateTime.Now;
        var info = Kinds.First(k => k.Kind == kind);
        var parts = info.Code.Split('^');
        var segs = new List<string?> { Msh(parts[0], parts[1], info.Structure, ts) };

        switch (kind)
        {
            case Hl7MessageKind.AdtA01:
            case Hl7MessageKind.AdtA04:
            case Hl7MessageKind.AdtA08:
                segs.Add($"EVN|{parts[1]}|{Ts(ts)}");
                segs.Add(Pid(p));
                segs.Add(Pv1());
                break;

            case Hl7MessageKind.OrmO01:
                segs.Add(Pid(p));
                segs.Add(Pv1());
                segs.Add(Orc(p, "NW", ts));
                segs.Add(Obr(p, ts, isResult: false));
                segs.Add(Nte(p));
                break;

            case Hl7MessageKind.OmlO21:
                segs.Add(Pid(p));
                segs.Add(Pv1());
                segs.Add(Orc(p, "NW", ts));
                segs.Add(Obr(p, ts, isResult: false));
                if (s.IsV25OrLater) segs.Add(Spm(p, ts));
                segs.Add(Nte(p));
                break;

            case Hl7MessageKind.OruR01:
                segs.Add(Pid(p));
                segs.Add(Pv1());
                segs.Add(Orc(p, "RE", ts));
                segs.Add(Obr(p, ts, isResult: true));
                segs.Add(Nte(p));
                segs.AddRange(ResultObxs(p, ts));
                if (s.IsV25OrLater) segs.Add(Spm(p, ts));
                break;

            case Hl7MessageKind.OulR22:
                segs.Add(Pid(p));
                segs.Add(Pv1());
                segs.Add(Spm(p, ts));
                segs.Add(Obr(p, ts, isResult: true));
                segs.Add(Orc(p, "RE", ts));
                segs.Add(Nte(p));
                segs.AddRange(ResultObxs(p, ts));
                break;

            case Hl7MessageKind.QbpQ11:
                segs.Add($"QPD|WOS^Work Order Step^IHE_LABTF|Q{NewControlId()}|{E(p.SpecimenId)}");
                segs.Add("RCP|I||R");
                break;
        }
        return string.Join("\r", segs.Where(x => !string.IsNullOrEmpty(x)));
    }

    /// <summary>Acknowledgement (original mode) for an incoming message.</summary>
    public string Ack(Hl7Message incoming, AutoAckMode mode, DateTime? timestamp = null)
    {
        var ts = timestamp ?? DateTime.Now;
        var code = mode switch { AutoAckMode.Error => "AE", AutoAckMode.Reject => "AR", _ => "AA" };
        var version = incoming.Version.Length > 0 ? incoming.Version : s.Hl7Version;
        var msh = string.Join("|",
            @"MSH|^~\&", E(s.SendingApplication), E(s.SendingFacility),
            E(incoming.SendingApplication), E(incoming.SendingFacility),
            Ts(ts), "", $"ACK^{E(incoming.TriggerEvent)}^ACK", NewControlId(), E(s.ProcessingId), E(version));
        var text = mode switch
        {
            AutoAckMode.Error => "Application error (simulated by emulator)",
            AutoAckMode.Reject => "Message rejected (simulated by emulator)",
            _ => "Message accepted",
        };
        var lines = new List<string> { msh, $"MSA|{code}|{E(incoming.ControlId)}|{E(text)}" };
        if (mode is AutoAckMode.Error or AutoAckMode.Reject)
            lines.Add(code == "AR" ? "ERR|||200^Unsupported message type^HL70357|E" : "ERR|||207^Application internal error^HL70357|E");
        return string.Join("\r", lines);
    }
}
