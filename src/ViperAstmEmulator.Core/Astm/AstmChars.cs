using System.Text;

namespace ViperAstmEmulator.Core.Astm;

/// <summary>ASTM E1381 / CLSI LIS1-A control characters and helpers.</summary>
public static class AstmChars
{
    public const byte STX = 0x02;
    public const byte ETX = 0x03;
    public const byte EOT = 0x04;
    public const byte ENQ = 0x05;
    public const byte ACK = 0x06;
    public const byte LF = 0x0A;
    public const byte CR = 0x0D;
    public const byte NAK = 0x15;
    public const byte ETB = 0x17;

    /// <summary>ASTM is an 8-bit byte protocol; Latin-1 maps bytes 1:1 to chars.</summary>
    public static readonly Encoding Latin1 = Encoding.Latin1;

    public static string Name(byte b) => b switch
    {
        STX => "<STX>",
        ETX => "<ETX>",
        EOT => "<EOT>",
        ENQ => "<ENQ>",
        ACK => "<ACK>",
        LF => "<LF>",
        CR => "<CR>",
        NAK => "<NAK>",
        ETB => "<ETB>",
        < 0x20 or 0x7F => $"<0x{b:X2}>",
        _ => ((char)b).ToString(),
    };

    /// <summary>Renders bytes with control characters made visible, e.g. "&lt;STX&gt;1H|...".</summary>
    public static string Visualize(ReadOnlySpan<byte> bytes)
    {
        var sb = new StringBuilder(bytes.Length + 16);
        foreach (var b in bytes) sb.Append(Name(b));
        return sb.ToString();
    }

    public static string Visualize(string text) => Visualize(Latin1.GetBytes(text));
}
