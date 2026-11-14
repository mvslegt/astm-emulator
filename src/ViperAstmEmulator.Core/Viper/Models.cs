using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace ViperAstmEmulator.Core.Viper;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
}

/// <summary>One reportable channel of an assay (a genotype / genotype group, or the internal control).</summary>
public sealed class TargetDefinition : ObservableObject
{
    private string _code = "";
    private string _name = "";
    private bool _isControl;
    private bool _enabled = true;

    /// <summary>Code sent in the Universal Test ID field (R.3), e.g. "HPV16".</summary>
    public string Code { get => _code; set => Set(ref _code, value); }
    public string Name { get => _name; set => Set(ref _name, value); }
    /// <summary>Internal/sample adequacy control (e.g. beta-globin).</summary>
    public bool IsControl { get => _isControl; set => Set(ref _isControl, value); }
    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }

    public TargetDefinition() { }
    public TargetDefinition(string code, string name, bool isControl = false) { _code = code; _name = name; _isControl = isControl; }

    /// <summary>BD Onclarity HPV extended genotyping channels as run on the BD Viper LT.</summary>
    public static List<TargetDefinition> OnclarityDefaults() =>
    [
        new("HPV16", "HPV 16"),
        new("HPV18", "HPV 18"),
        new("HPV45", "HPV 45"),
        new("HPV31", "HPV 31"),
        new("HPV51", "HPV 51"),
        new("HPV52", "HPV 52"),
        new("HPV33_58", "HPV 33/58"),
        new("HPV35_39_68", "HPV 35/39/68"),
        new("HPV56_59_66", "HPV 56/59/66"),
        new("BC", "Beta-globin control (BC)", isControl: true),
    ];
}

public static class ResultValues
{
    public const string Positive = "POS";
    public const string Negative = "NEG";
    public const string Indeterminate = "IND";
    public const string Invalid = "INV";
    public static readonly string[] All = [Positive, Negative, Indeterminate, Invalid];
}

public sealed class TargetResult : ObservableObject
{
    private string _result = ResultValues.Negative;
    private string _ct = "";

    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public bool IsControl { get; set; }

    public string Result { get => _result; set => Set(ref _result, value); }
    /// <summary>Cycle threshold, blank when not detected.</summary>
    public string Ct { get => _ct; set => Set(ref _ct, value); }
}

public sealed class Sample : ObservableObject
{
    private string _specimenId = "";
    private string _patientId = "";
    private string _lastName = "";
    private string _firstName = "";
    private string _birthDate = "";
    private string _sex = "F";
    private string _rack = "";
    private string _position = "";
    private string _comment = "";
    private string _status = "New";
    private DateTime? _lastSent;

    public string SpecimenId { get => _specimenId; set => Set(ref _specimenId, value); }
    public string PatientId { get => _patientId; set => Set(ref _patientId, value); }
    public string LastName { get => _lastName; set => Set(ref _lastName, value); }
    public string FirstName { get => _firstName; set => Set(ref _firstName, value); }
    /// <summary>YYYYMMDD</summary>
    public string BirthDate { get => _birthDate; set => Set(ref _birthDate, value); }
    public string Sex { get => _sex; set => Set(ref _sex, value); }
    public string Rack { get => _rack; set => Set(ref _rack, value); }
    public string Position { get => _position; set => Set(ref _position, value); }
    public string Comment { get => _comment; set => Set(ref _comment, value); }
    public string Status { get => _status; set => Set(ref _status, value); }
    public DateTime? LastSent { get => _lastSent; set => Set(ref _lastSent, value); }

    [JsonIgnore]
    public ObservableCollection<TargetResult> Results { get; } = new();

    public string OverallResult => ComputeOverall(Results);

    /// <summary>Short text such as "POS (16, 52)" for the sample list.</summary>
    public string Summary
    {
        get
        {
            var pos = Results.Where(r => !r.IsControl && r.Result == ResultValues.Positive).Select(r => r.Name.Replace("HPV ", "")).ToList();
            var overall = OverallResult;
            return pos.Count > 0 ? $"{overall} ({string.Join(", ", pos)})" : overall;
        }
    }

    public Sample()
    {
        Results.CollectionChanged += OnResultsChanged;
    }

    public void SetTargets(IEnumerable<TargetDefinition> targets)
    {
        Results.Clear();
        foreach (var t in targets.Where(t => t.Enabled))
            Results.Add(new TargetResult { Code = t.Code, Name = t.Name, IsControl = t.IsControl });
    }

    private void OnResultsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null) foreach (TargetResult r in e.NewItems) r.PropertyChanged += (_, _) => RaiseSummary();
        RaiseSummary();
    }

    private void RaiseSummary()
    {
        OnPropertyChanged(nameof(OverallResult));
        OnPropertyChanged(nameof(Summary));
    }

    /// <summary>
    /// Any HPV target positive → POS (control not required).
    /// Otherwise a failed control → INV, any indeterminate target → IND, else NEG.
    /// </summary>
    public static string ComputeOverall(IEnumerable<TargetResult> results)
    {
        var list = results.ToList();
        var hpv = list.Where(r => !r.IsControl).ToList();
        if (hpv.Any(r => r.Result == ResultValues.Positive)) return ResultValues.Positive;
        if (hpv.Any(r => r.Result == ResultValues.Invalid)) return ResultValues.Invalid;
        var control = list.FirstOrDefault(r => r.IsControl);
        if (control != null && control.Result != ResultValues.Positive) return ResultValues.Invalid;
        if (hpv.Any(r => r.Result == ResultValues.Indeterminate)) return ResultValues.Indeterminate;
        return ResultValues.Negative;
    }
}
