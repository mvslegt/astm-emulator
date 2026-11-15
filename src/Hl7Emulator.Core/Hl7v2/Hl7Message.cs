using System.Text;

namespace Hl7Emulator.Core.Hl7v2;

/// <summary>
/// Lightweight HL7 v2 message: segments split into fields, with 1-based field access
/// following HL7 numbering (MSH-1 is the field separator itself).
/// </summary>
public sealed class Hl7Message
{
    private readonly List<string[]> _segments = new();

    public char FieldSeparator { get; private set; } = '|';
    public char ComponentSeparator { get; private set; } = '^';
    public char RepetitionSeparator { get; private set; } = '~';
    public char EscapeCharacter { get; private set; } = '\\';
    public char SubcomponentSeparator { get; private set; } = '&';

    public IReadOnlyList<string[]> Segments => _segments;

    public static Hl7Message Parse(string text)
    {
        var msg = new Hl7Message();
        var lines = text.Replace("\r\n", "\r").Replace('\n', '\r')
                        .Split('\r', StringSplitOptions.RemoveEmptyEntries)
                        .Select(l => l.Trim('\v', '\x1c', ' '))
                        .Where(l => l.Length >= 3)
                        .ToList();

        var msh = lines.FirstOrDefault(l => l.StartsWith("MSH", StringComparison.Ordinal));
        if (msh != null && msh.Length >= 8)
        {
            msg.FieldSeparator = msh[3];
            msg.ComponentSeparator = msh[4];
            msg.RepetitionSeparator = msh[5];
            msg.EscapeCharacter = msh[6];
            msg.SubcomponentSeparator = msh[7];
        }
        foreach (var l in lines) msg._segments.Add(l.Split(msg.FieldSeparator));
        return msg;
    }

    public IEnumerable<string[]> All(string segmentId) => _segments.Where(s => s[0] == segmentId);

    public bool Has(string segmentId) => _segments.Any(s => s[0] == segmentId);

    /// <summary>Raw field value (first repetition), e.g. Get("PID", 5) = "Doe^Jane".</summary>
    public string GetField(string segmentId, int field, int occurrence = 1)
    {
        var seg = All(segmentId).Skip(occurrence - 1).FirstOrDefault();
        return seg == null ? "" : FieldOf(seg, field);
    }

    /// <summary>Unescaped component value, e.g. Get("MSH", 9, 1) = "ORU".</summary>
    public string Get(string segmentId, int field, int component = 1, int occurrence = 1)
    {
        var f = GetField(segmentId, field, occurrence);
        var comps = f.Split(ComponentSeparator);
        return component - 1 < comps.Length ? Unescape(comps[component - 1]) : "";
    }

    public string FieldOf(string[] segment, int field)
    {
        // MSH-1 is the separator; MSH-2 is parts[1]. For other segments X-n is parts[n].
        string raw;
        if (segment[0] == "MSH")
        {
            if (field == 1) return FieldSeparator.ToString();
            raw = field - 1 < segment.Length ? segment[field - 1] : "";
            if (field == 2) return raw;
        }
        else
        {
            raw = field < segment.Length ? segment[field] : "";
        }
        var rep = raw.IndexOf(RepetitionSeparator);
        return rep >= 0 ? raw[..rep] : raw;
    }

    public string ComponentOf(string[] segment, int field, int component)
    {
        var comps = FieldOf(segment, field).Split(ComponentSeparator);
        return component - 1 < comps.Length ? Unescape(comps[component - 1]) : "";
    }

    public string MessageCode => Get("MSH", 9, 1);
    public string TriggerEvent => Get("MSH", 9, 2);
    public string MessageType => TriggerEvent.Length > 0 ? $"{MessageCode}^{TriggerEvent}" : MessageCode;
    public string ControlId => Get("MSH", 10);
    public string Version => Get("MSH", 12, 1);
    public string SendingApplication => Get("MSH", 3);
    public string SendingFacility => Get("MSH", 4);
    public string ReceivingApplication => Get("MSH", 5);
    public string ReceivingFacility => Get("MSH", 6);

    public bool IsAck => MessageCode == "ACK" || (Has("MSA") && !Has("PID") && !Has("OBR"));
    public string AckCode => Get("MSA", 1);
    public string AckControlId => Get("MSA", 2);
    public string AckText => Get("MSA", 3);

    public override string ToString() => string.Join("\r", _segments.Select(s => string.Join(FieldSeparator, s)));

    /// <summary>One line per segment, for display.</summary>
    public string ToDisplayText() => string.Join(Environment.NewLine, _segments.Select(s => string.Join(FieldSeparator, s)));

    public string Unescape(string value)
    {
        if (value.IndexOf(EscapeCharacter) < 0) return value;
        var sb = new StringBuilder(value.Length);
        for (int i = 0; i < value.Length; i++)
        {
            if (value[i] == EscapeCharacter)
            {
                int end = value.IndexOf(EscapeCharacter, i + 1);
                if (end > i)
                {
                    var seq = value.Substring(i + 1, end - i - 1);
                    sb.Append(seq switch
                    {
                        "F" => FieldSeparator.ToString(),
                        "S" => ComponentSeparator.ToString(),
                        "R" => RepetitionSeparator.ToString(),
                        "E" => EscapeCharacter.ToString(),
                        "T" => SubcomponentSeparator.ToString(),
                        ".br" => "\n",
                        _ => "",
                    });
                    i = end;
                    continue;
                }
            }
            sb.Append(value[i]);
        }
        return sb.ToString();
    }

    /// <summary>Escapes delimiters in a data value using the default encoding characters ^~\&amp;.</summary>
    public static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            sb.Append(c switch
            {
                '\\' => @"\E\",
                '|' => @"\F\",
                '^' => @"\S\",
                '~' => @"\R\",
                '&' => @"\T\",
                '\r' or '\n' => @"\.br\",
                _ => c.ToString(),
            });
        }
        return sb.ToString();
    }

    /// <summary>Normalises editor text (one segment per line) to HL7 wire format (CR separated).</summary>
    public static string FromDisplayText(string text) =>
        string.Join("\r", text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n')
            .Select(l => l.TrimEnd()).Where(l => l.Length > 0));
}
