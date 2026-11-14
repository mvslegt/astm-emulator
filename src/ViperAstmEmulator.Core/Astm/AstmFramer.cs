using System.Text;
using static ViperAstmEmulator.Core.Astm.AstmChars;

namespace ViperAstmEmulator.Core.Astm;

public readonly record struct ParsedFrame(bool IsValid, int FrameNumber, string Text, bool IsFinal, string? Error);

/// <summary>
/// Builds and parses LIS1-A (ASTM E1381) frames:
/// <c>&lt;STX&gt; FN text &lt;ETB|ETX&gt; C1 C2 &lt;CR&gt;&lt;LF&gt;</c>.
/// </summary>
public static class AstmFramer
{
    /// <summary>Max frame is 247 chars; 7 are overhead, so 240 chars of text.</summary>
    public const int MaxFrameText = 240;

    /// <summary>Checksum: sum of bytes from FN through ETX/ETB inclusive, modulo 256, as two uppercase hex digits.</summary>
    public static string Checksum(ReadOnlySpan<byte> fnThroughTerminator)
    {
        int sum = 0;
        foreach (var b in fnThroughTerminator) sum += b;
        return (sum & 0xFF).ToString("X2");
    }

    public static byte[] BuildFrame(int frameNumber, string text, bool final, bool corruptChecksum = false)
    {
        var textBytes = Latin1.GetBytes(text);
        var frame = new byte[textBytes.Length + 7];
        frame[0] = STX;
        frame[1] = (byte)('0' + (frameNumber % 8));
        textBytes.CopyTo(frame, 2);
        int t = 2 + textBytes.Length;
        frame[t] = final ? ETX : ETB;
        var cs = Checksum(frame.AsSpan(1, t));
        if (corruptChecksum) cs = cs == "00" ? "01" : "00";
        frame[t + 1] = (byte)cs[0];
        frame[t + 2] = (byte)cs[1];
        frame[t + 3] = CR;
        frame[t + 4] = LF;
        return frame;
    }

    /// <summary>
    /// Splits records into frames. Each record is terminated with CR. Frame numbers start at 1 and wrap modulo 8.
    /// </summary>
    /// <param name="recordPerFrame">
    /// true: every record starts a new frame and ends with ETX (long records continue over ETB frames).
    /// false: the whole message is one stream split every <paramref name="maxText"/> chars.
    /// </param>
    public static List<byte[]> BuildFrames(IEnumerable<string> records, int maxText = MaxFrameText, bool recordPerFrame = true)
    {
        if (maxText < 1 || maxText > MaxFrameText) maxText = MaxFrameText;
        var frames = new List<byte[]>();
        int fn = 1;

        void AddChunks(string block)
        {
            for (int pos = 0; pos < block.Length; pos += maxText)
            {
                int len = Math.Min(maxText, block.Length - pos);
                bool final = pos + len >= block.Length;
                frames.Add(BuildFrame(fn, block.Substring(pos, len), final));
                fn = (fn + 1) % 8;
            }
        }

        var list = records.Where(r => r.Length > 0).Select(r => r.TrimEnd('\r', '\n') + "\r").ToList();
        if (recordPerFrame)
            foreach (var r in list) AddChunks(r);
        else
            AddChunks(string.Concat(list));
        return frames;
    }

    /// <summary>Parses a complete frame starting with STX and ending with LF.</summary>
    public static ParsedFrame Parse(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 7) return Bad("frame too short");
        if (frame[0] != STX) return Bad("missing STX");
        if (frame[^1] != LF || frame[^2] != CR) return Bad("missing CR LF trailer");
        int fn = frame[1] - '0';
        if (fn is < 0 or > 7) return Bad($"invalid frame number {Name(frame[1])}");
        int t = frame.Length - 5;
        byte term = frame[t];
        if (term != ETX && term != ETB) return Bad("missing ETX/ETB");
        var expected = Checksum(frame.Slice(1, t));
        var actual = Latin1.GetString(frame.Slice(t + 1, 2)).ToUpperInvariant();
        var text = Latin1.GetString(frame.Slice(2, t - 2));
        if (expected != actual) return new ParsedFrame(false, fn, text, term == ETX, $"checksum mismatch (got {actual}, expected {expected})");
        return new ParsedFrame(true, fn, text, term == ETX, null);

        static ParsedFrame Bad(string e) => new(false, -1, "", false, e);
    }

    /// <summary>Splits reassembled message text into records (CR separated).</summary>
    public static List<string> SplitRecords(string message) =>
        message.Split('\r', StringSplitOptions.RemoveEmptyEntries)
               .Select(r => r.Trim('\n'))
               .Where(r => r.Length > 0)
               .ToList();
}
