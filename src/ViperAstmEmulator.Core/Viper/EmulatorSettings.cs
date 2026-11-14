using System.Collections.ObjectModel;
using System.IO.Ports;
using System.Text.Json;
using System.Text.Json.Serialization;
using ViperAstmEmulator.Core.Astm;

namespace ViperAstmEmulator.Core.Viper;

public enum CtReporting
{
    /// <summary>Qualitative result only.</summary>
    None,
    /// <summary>Extra R record per target with test ID <c>^^^CODE^CT</c>.</summary>
    SeparateRecord,
    /// <summary>Ct as second component of the result value: <c>POS^28.4</c>.</summary>
    ResultComponent,
}

/// <summary>All user-configurable settings, persisted as JSON.</summary>
public sealed class EmulatorSettings
{
    // Connection
    public ConnectionMode Mode { get; set; } = ConnectionMode.TcpClient;
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 5000;
    public string BindAddress { get; set; } = "";
    public bool AutoReconnect { get; set; } = true;
    public string ComPort { get; set; } = "COM1";
    public int BaudRate { get; set; } = 9600;
    public Parity Parity { get; set; } = Parity.None;
    public int DataBits { get; set; } = 8;
    public StopBits StopBits { get; set; } = StopBits.One;
    public Handshake Handshake { get; set; } = Handshake.None;

    // Low level
    public bool RecordPerFrame { get; set; } = true;
    public int MaxFrameText { get; set; } = AstmFramer.MaxFrameText;
    public int ReplyTimeoutSeconds { get; set; } = 15;
    public int ReceiveTimeoutSeconds { get; set; } = 30;
    public bool CorruptFirstFrameChecksum { get; set; }
    public bool NakFirstReceivedFrame { get; set; }

    // Instrument identity (Header record)
    public string InstrumentName { get; set; } = "BD Viper LT";
    public string InstrumentSerial { get; set; } = "VLT0001";
    public string SoftwareVersion { get; set; } = "3.0";
    public string ReceiverId { get; set; } = "LIS";
    public string ProcessingId { get; set; } = "P";
    public string VersionNumber { get; set; } = "LIS2-A2";
    public string OperatorId { get; set; } = "OPERATOR";

    // Message content
    public string AssayCode { get; set; } = "HPV";
    public string SpecimenType { get; set; } = "Cervical";
    public bool IncludePatientDemographics { get; set; } = true;
    public bool IncludeOverallResult { get; set; } = true;
    public bool IncludeControlResult { get; set; } = true;
    public CtReporting CtReporting { get; set; } = CtReporting.SeparateRecord;
    public bool IncludeRackPosition { get; set; } = true;
    public bool TrimTrailingDelimiters { get; set; } = true;
    /// <summary>All samples in one H..L message (true) or one message per sample (false).</summary>
    public bool BatchSamples { get; set; }

    // Behaviour
    public bool AddReceivedOrdersToList { get; set; } = true;
    public int AutoSendIntervalSeconds { get; set; } = 30;
    public string SpecimenIdPrefix { get; set; } = "HPV";
    public int NextSpecimenNumber { get; set; } = 1;

    public ObservableCollection<TargetDefinition> Targets { get; set; } = new(TargetDefinition.OnclarityDefaults());

    public AstmLinkOptions ToLinkOptions() => new()
    {
        RecordPerFrame = RecordPerFrame,
        MaxFrameText = MaxFrameText,
        ReplyTimeout = TimeSpan.FromSeconds(Math.Max(1, ReplyTimeoutSeconds)),
        ReceiveTimeout = TimeSpan.FromSeconds(Math.Max(1, ReceiveTimeoutSeconds)),
        CorruptFirstFrameChecksum = CorruptFirstFrameChecksum,
        NakFirstReceivedFrame = NakFirstReceivedFrame,
    };

    public IAstmTransport CreateTransport() => Mode switch
    {
        ConnectionMode.TcpClient => new TcpClientTransport(Host, Port),
        ConnectionMode.TcpServer => new TcpServerTransport(Port, BindAddress),
        ConnectionMode.Serial => new SerialTransport(ComPort, BaudRate, Parity, DataBits, StopBits, Handshake),
        _ => throw new ArgumentOutOfRangeException(),
    };

    // ---------------- Persistence ----------------

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ViperAstmEmulator", "settings.json");

    public static EmulatorSettings Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (File.Exists(path))
            {
                var s = JsonSerializer.Deserialize<EmulatorSettings>(File.ReadAllText(path), JsonOptions);
                if (s != null)
                {
                    if (s.Targets.Count == 0) s.Targets = new(TargetDefinition.OnclarityDefaults());
                    return s;
                }
            }
        }
        catch { /* fall back to defaults */ }
        return new EmulatorSettings();
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
    }
}
