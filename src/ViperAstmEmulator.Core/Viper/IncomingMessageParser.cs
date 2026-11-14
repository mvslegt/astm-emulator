namespace ViperAstmEmulator.Core.Viper;

public sealed record ReceivedOrder(string SpecimenId, string PatientId, string LastName, string FirstName, string BirthDate, string Sex, string TestCode, string ActionCode);

/// <summary>Minimal LIS2-A2 parsing of messages sent by the LIS to the instrument (orders, e.g. in reply to a host query).</summary>
public static class IncomingMessageParser
{
    public static string Unescape(string v) =>
        v.Replace("&F&", "|").Replace("&R&", "\\").Replace("&S&", "^").Replace("&E&", "&");

    private static string Field(string[] f, int fieldNumber) => fieldNumber - 1 < f.Length ? f[fieldNumber - 1] : "";

    private static string Component(string field, int componentNumber)
    {
        var c = field.Split('^');
        return componentNumber - 1 < c.Length ? Unescape(c[componentNumber - 1]) : "";
    }

    public static List<ReceivedOrder> ParseOrders(IReadOnlyList<string> records)
    {
        var orders = new List<ReceivedOrder>();
        string pid = "", last = "", first = "", dob = "", sex = "";
        foreach (var rec in records)
        {
            if (rec.Length < 1) continue;
            // Records may be prefixed with a frame number if the LIS mis-frames; tolerate that.
            var r = rec.Length > 1 && char.IsDigit(rec[0]) ? rec[1..] : rec;
            var f = r.Split('|');
            switch (char.ToUpperInvariant(f[0].FirstOrDefault()))
            {
                case 'P':
                    pid = Unescape(Field(f, 3));
                    if (pid.Length == 0) pid = Unescape(Field(f, 4));
                    last = Component(Field(f, 6), 1);
                    first = Component(Field(f, 6), 2);
                    dob = Unescape(Field(f, 8));
                    sex = Unescape(Field(f, 9));
                    break;
                case 'O':
                    var spec = Component(Field(f, 3), 1);
                    if (spec.Length == 0) continue;
                    var test = Field(f, 5).Split('^').LastOrDefault(x => x.Length > 0) ?? "";
                    orders.Add(new ReceivedOrder(spec, pid, last, first, dob, sex, Unescape(test), Unescape(Field(f, 12))));
                    break;
            }
        }
        return orders;
    }

    /// <summary>One-line summary of the record types in a message, e.g. "H P O L".</summary>
    public static string Describe(IReadOnlyList<string> records) =>
        string.Join(" ", records.Select(r => r.Length > 0 ? r[0].ToString() : "?"));
}
