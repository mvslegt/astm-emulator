using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ViperAstmEmulator.Core.Viper;

namespace Hl7Emulator.Core;

public enum MllpMode { Client, Server }

/// <summary>How the emulator answers HL7 v2 messages it receives.</summary>
public enum AutoAckMode
{
    /// <summary>MSA-1 = AA (application accept).</summary>
    Accept,
    /// <summary>MSA-1 = AE (application error).</summary>
    Error,
    /// <summary>MSA-1 = AR (application reject).</summary>
    Reject,
    /// <summary>Do not send an acknowledgement.</summary>
    None,
}

/// <summary>How the built-in FHIR server answers requests.</summary>
public enum FhirServerResponseMode
{
    /// <summary>Store the resources and answer 200/201.</summary>
    Accept,
    /// <summary>Answer 400 Bad Request with an OperationOutcome.</summary>
    RejectBadRequest,
    /// <summary>Answer 500 Internal Server Error with an OperationOutcome.</summary>
    ServerError,
}

/// <summary>All persisted settings of the HL7 / FHIR emulator.</summary>
public sealed class Hl7Settings : ISpecimenSettings
{
    // ---- Identity (MSH-3..6, FHIR MessageHeader) ----
    public string SendingApplication { get; set; } = "HL7EMU";
    public string SendingFacility { get; set; } = "LAB";
    public string ReceivingApplication { get; set; } = "LIS";
    public string ReceivingFacility { get; set; } = "HOSPITAL";
    public string InstrumentId { get; set; } = "BD Viper LT VLT0001";

    // ---- HL7 v2 ----
    public string Hl7Version { get; set; } = "2.5.1";
    public string ProcessingId { get; set; } = "P";
    public string CharacterSet { get; set; } = "UTF-8";
    public bool IncludeCharsetInMsh { get; set; }

    // ---- MLLP ----
    public MllpMode MllpMode { get; set; } = MllpMode.Client;
    public string MllpHost { get; set; } = "127.0.0.1";
    public int MllpPort { get; set; } = 2575;
    public string MllpBindAddress { get; set; } = "";
    public bool MllpAutoReconnect { get; set; } = true;
    public bool WaitForAck { get; set; } = true;
    public int AckTimeoutSeconds { get; set; } = 30;
    public AutoAckMode AutoAckMode { get; set; } = AutoAckMode.Accept;

    // ---- Content ----
    public string AssayCode { get; set; } = "HPV";
    public string AssayName { get; set; } = "HPV Onclarity genotyping";
    public string SpecimenType { get; set; } = "Cervical";
    public bool IncludeCt { get; set; } = true;
    public bool IncludeControlResult { get; set; } = true;
    public bool IncludeOverallResult { get; set; } = true;
    public bool AddReceivedOrdersToList { get; set; } = true;
    public string SpecimenIdPrefix { get; set; } = "HL7";
    public int NextSpecimenNumber { get; set; } = 1;
    public ObservableCollection<TargetDefinition> Targets { get; set; } = new(TargetDefinition.OnclarityDefaults());

    // ---- FHIR client ----
    public string FhirBaseUrl { get; set; } = "http://localhost:8080/fhir";
    /// <summary>Optional bearer token sent as Authorization header. Stored in plain text in settings.json.</summary>
    public string FhirBearerToken { get; set; } = "";
    public int FhirTimeoutSeconds { get; set; } = 30;

    // ---- FHIR server (receiving) ----
    /// <summary>"localhost" works without admin rights; "+" listens on all interfaces but needs a URL ACL (netsh http add urlacl).</summary>
    public string FhirServerHost { get; set; } = "localhost";
    public int FhirServerPort { get; set; } = 8090;
    public string FhirServerPath { get; set; } = "fhir";
    public FhirServerResponseMode FhirServerResponseMode { get; set; } = FhirServerResponseMode.Accept;

    // ---- Code systems / identifier systems used in FHIR resources ----
    public string LocalCodeSystem { get; set; } = "http://example.org/fhir/CodeSystem/hpv-onclarity";
    public string PatientIdSystem { get; set; } = "http://example.org/fhir/sid/patient-id";
    public string SpecimenIdSystem { get; set; } = "http://example.org/fhir/sid/specimen-id";
    public string OrderIdSystem { get; set; } = "http://example.org/fhir/sid/order-id";
    public string MessageEventSystem { get; set; } = "http://example.org/fhir/message-events";

    [JsonIgnore]
    public Encoding TextEncoding => CharacterSet.Equals("ISO-8859-1", StringComparison.OrdinalIgnoreCase) ? Encoding.Latin1 : new UTF8Encoding(false);

    [JsonIgnore]
    public bool IsV25OrLater => Version.TryParse(Hl7Version, out var v) && v >= new Version(2, 5);

    // ---- Persistence ----

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Hl7Emulator", "settings.json");

    public static Hl7Settings Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (File.Exists(path))
            {
                var s = JsonSerializer.Deserialize<Hl7Settings>(File.ReadAllText(path), JsonOptions);
                if (s != null)
                {
                    if (s.Targets.Count == 0) s.Targets = new(TargetDefinition.OnclarityDefaults());
                    return s;
                }
            }
        }
        catch { /* defaults */ }
        return new Hl7Settings();
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
    }
}
