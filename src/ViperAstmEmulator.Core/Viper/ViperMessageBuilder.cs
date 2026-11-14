using System.Globalization;
using System.Text;

namespace ViperAstmEmulator.Core.Viper;

/// <summary>
/// Builds ASTM E1394 / CLSI LIS2-A2 records the way a BD Viper LT reports BD Onclarity HPV results:
/// H, then per specimen P, O, [C], R (one per genotype channel, optional Ct and overall), L.
/// Delimiters: field '|', repeat '\', component '^', escape '&amp;'.
/// </summary>
public sealed class ViperMessageBuilder(EmulatorSettings s)
{
    public const string DateFormat = "yyyyMMddHHmmss";

    /// <summary>Escapes delimiter characters inside a data value (LIS2-A2 §7.2).</summary>
    public static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            sb.Append(c switch
            {
                '&' => "&E&",
                '|' => "&F&",
                '\\' => "&R&",
                '^' => "&S&",
                '\r' or '\n' => " ",
                _ => c.ToString(),
            });
        }
        return sb.ToString();
    }

    /// <summary>Joins escaped components with '^', dropping trailing empty components.</summary>
    public static string Comp(params string?[] components)
    {
        int last = components.Length - 1;
        while (last >= 0 && string.IsNullOrEmpty(components[last])) last--;
        return string.Join("^", components.Take(last + 1).Select(Escape));
    }

    /// <summary>Joins fields with '|'. Fields are passed already escaped / componentised.</summary>
    private string Rec(params string[] fields)
    {
        int last = fields.Length - 1;
        if (s.TrimTrailingDelimiters)
            while (last > 0 && fields[last].Length == 0) last--;
        return string.Join("|", fields.Take(last + 1));
    }

    private static string Num(int n) => n.ToString(CultureInfo.InvariantCulture);

    public string Header(DateTime ts) => Rec(
        "H",                                                            // 1 record type
        @"\^&",                                                         // 2 delimiters
        "",                                                             // 3 message control ID
        "",                                                             // 4 access password
        Comp(s.InstrumentName, s.InstrumentSerial, s.SoftwareVersion),  // 5 sender name/ID
        "",                                                             // 6 sender address
        "",                                                             // 7 reserved
        "",                                                             // 8 sender phone
        "",                                                             // 9 characteristics
        Escape(s.ReceiverId),                                           // 10 receiver ID
        "",                                                             // 11 comment
        Escape(s.ProcessingId),                                         // 12 processing ID
        Escape(s.VersionNumber),                                        // 13 version
        ts.ToString(DateFormat, CultureInfo.InvariantCulture));         // 14 date/time of message

    public string Terminator(string code = "N") => Rec("L", "1", code);

    public string Patient(int seq, Sample p) => s.IncludePatientDemographics
        ? Rec("P", Num(seq), Escape(p.PatientId), "", "", Comp(p.LastName, p.FirstName), "", Escape(p.BirthDate), Escape(p.Sex))
        : Rec("P", Num(seq));

    public string Order(int seq, Sample p, DateTime ts)
    {
        var f = new string[26];
        Array.Fill(f, "");
        f[0] = "O";
        f[1] = Num(seq);
        f[2] = Escape(p.SpecimenId);                                          // 3 specimen ID
        if (s.IncludeRackPosition) f[3] = Comp(p.Rack, p.Position);           // 4 instrument specimen ID
        f[4] = "^^^" + Escape(s.AssayCode);                                    // 5 universal test ID
        f[5] = "R";                                                            // 6 priority
        f[15] = Escape(s.SpecimenType);                                        // 16 specimen descriptor
        f[22] = ts.ToString(DateFormat, CultureInfo.InvariantCulture);         // 23 date/time results reported
        f[25] = "F";                                                           // 26 report type: final
        return Rec(f);
    }

    public string Result(int seq, string testCode, string value, DateTime started, DateTime completed, string units = "", string flag = "")
        => Rec(
            "R",                                                              // 1
            Num(seq),                                                         // 2
            "^^^" + testCode,                                                 // 3 universal test ID
            value,                                                            // 4 measurement value
            Escape(units),                                                    // 5 units
            "",                                                               // 6 reference range
            Escape(flag),                                                     // 7 abnormal flag
            "",                                                               // 8 nature of abnormality
            "F",                                                              // 9 result status
            "",                                                               // 10 date of change
            Escape(s.OperatorId),                                             // 11 operator
            started.ToString(DateFormat, CultureInfo.InvariantCulture),       // 12 started
            completed.ToString(DateFormat, CultureInfo.InvariantCulture),     // 13 completed
            Comp(s.InstrumentName, s.InstrumentSerial));                      // 14 instrument ID

    public string Comment(int seq, string text) => Rec("C", Num(seq), "I", Escape(text), "G");

    /// <summary>Patient/order/result block for one specimen (no H/L).</summary>
    public List<string> SampleBlock(int patientSeq, Sample p, DateTime ts)
    {
        var records = new List<string> { Patient(patientSeq, p), Order(1, p, ts) };
        if (!string.IsNullOrWhiteSpace(p.Comment)) records.Add(Comment(1, p.Comment));

        var started = ts.AddMinutes(-150);
        int seq = 1;
        foreach (var r in p.Results)
        {
            if (r.IsControl && !s.IncludeControlResult) continue;
            var code = Escape(r.Code);
            bool hasCt = !string.IsNullOrWhiteSpace(r.Ct);
            string value = Escape(r.Result);
            if (s.CtReporting == CtReporting.ResultComponent && hasCt) value += "^" + Escape(r.Ct);
            records.Add(Result(seq++, code, value, started, ts));
            if (s.CtReporting == CtReporting.SeparateRecord && hasCt)
                records.Add(Result(seq++, code + "^CT", Escape(r.Ct), started, ts));
        }
        if (s.IncludeOverallResult)
            records.Add(Result(seq, Escape(s.AssayCode), Escape(p.OverallResult), started, ts));
        return records;
    }

    /// <summary>Complete result message (H … L) for one or more specimens.</summary>
    public List<string> ResultMessage(IEnumerable<Sample> samples, DateTime? timestamp = null)
    {
        var ts = timestamp ?? DateTime.Now;
        var records = new List<string> { Header(ts) };
        int pSeq = 1;
        foreach (var smp in samples) records.AddRange(SampleBlock(pSeq++, smp, ts));
        records.Add(Terminator());
        return records;
    }

    /// <summary>Host query (Q record) asking the LIS for orders for the given specimen IDs.</summary>
    public List<string> HostQuery(IEnumerable<string> specimenIds, DateTime? timestamp = null)
    {
        var ts = timestamp ?? DateTime.Now;
        var records = new List<string> { Header(ts) };
        int seq = 1;
        foreach (var id in specimenIds)
            records.Add(Rec("Q", Num(seq++), "^" + Escape(id), "", "^^^ALL", "", "", "", "", "", "", "", "O"));
        records.Add(Terminator());
        return records;
    }

    public static string ToDisplayText(IEnumerable<string> records) => string.Join(Environment.NewLine, records);

    public static List<string> FromDisplayText(string text) =>
        text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n')
            .Select(l => l.TrimEnd())
            .Where(l => l.Length > 0)
            .ToList();
}
