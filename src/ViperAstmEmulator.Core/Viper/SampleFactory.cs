using System.Globalization;

namespace ViperAstmEmulator.Core.Viper;

public enum Scenario
{
    Random,
    Negative,
    Hpv16Positive,
    Hpv18Positive,
    Hpv45Positive,
    OtherHighRiskPositive,
    MultipleGenotypes,
    InvalidControlFailure,
    Indeterminate,
}

/// <summary>Creates demo specimens and applies result scenarios.</summary>
/// <summary>Settings needed to create specimens; shared by the ASTM and HL7 emulators.</summary>
public interface ISpecimenSettings
{
    System.Collections.ObjectModel.ObservableCollection<TargetDefinition> Targets { get; }
    string SpecimenIdPrefix { get; }
    int NextSpecimenNumber { get; set; }
}

public sealed class SampleFactory(ISpecimenSettings settings, Random? rng = null)
{
    private readonly Random _rng = rng ?? new Random();

    private static readonly string[] LastNames = ["Jansen", "de Vries", "Bakker", "Visser", "Smit", "Meijer", "Mulder", "de Boer", "Bos", "Peters", "Hendriks", "Dekker"];
    private static readonly string[] FirstNames = ["Anna", "Emma", "Sophie", "Julia", "Lisa", "Eva", "Sara", "Maria", "Laura", "Fleur", "Noor", "Iris"];

    public Sample Create(Scenario scenario = Scenario.Random)
    {
        int n = settings.NextSpecimenNumber++;
        var s = new Sample
        {
            SpecimenId = $"{settings.SpecimenIdPrefix}{n:000000}",
            PatientId = $"PAT{_rng.Next(100000, 999999)}",
            LastName = LastNames[_rng.Next(LastNames.Length)],
            FirstName = FirstNames[_rng.Next(FirstNames.Length)],
            BirthDate = DateTime.Today.AddYears(-_rng.Next(25, 66)).AddDays(-_rng.Next(365)).ToString("yyyyMMdd", CultureInfo.InvariantCulture),
            Sex = "F",
            Rack = $"R{(n - 1) / 24 + 1:00}",
            Position = (((n - 1) % 24) + 1).ToString(CultureInfo.InvariantCulture),
        };
        s.SetTargets(settings.Targets);
        Apply(s, scenario);
        return s;
    }

    public void Apply(Sample s, Scenario scenario)
    {
        if (scenario == Scenario.Random) scenario = PickRandom();
        var hpv = s.Results.Where(r => !r.IsControl).ToList();
        var control = s.Results.FirstOrDefault(r => r.IsControl);

        foreach (var r in hpv) SetNeg(r);
        if (control != null) SetPos(control, 22, 30);
        s.Comment = "";

        void Positive(params string[] codes)
        {
            foreach (var c in codes)
            {
                var r = hpv.FirstOrDefault(x => x.Code.Equals(c, StringComparison.OrdinalIgnoreCase));
                if (r != null) SetPos(r, 21, 36);
            }
        }

        switch (scenario)
        {
            case Scenario.Negative:
                break;
            case Scenario.Hpv16Positive:
                Positive("HPV16");
                break;
            case Scenario.Hpv18Positive:
                Positive("HPV18");
                break;
            case Scenario.Hpv45Positive:
                Positive("HPV45");
                break;
            case Scenario.OtherHighRiskPositive:
            {
                var others = hpv.Where(r => r.Code is not ("HPV16" or "HPV18" or "HPV45")).ToList();
                if (others.Count > 0) SetPos(others[_rng.Next(others.Count)], 24, 37);
                break;
            }
            case Scenario.MultipleGenotypes:
                foreach (var r in hpv.OrderBy(_ => _rng.Next()).Take(_rng.Next(2, 4))) SetPos(r, 21, 37);
                break;
            case Scenario.InvalidControlFailure:
                if (control != null) { control.Result = ResultValues.Negative; control.Ct = ""; }
                s.Comment = "Internal control failure - retest";
                break;
            case Scenario.Indeterminate:
            {
                var r = hpv[_rng.Next(hpv.Count)];
                r.Result = ResultValues.Indeterminate;
                r.Ct = Ct(38, 40);
                break;
            }
        }
    }

    private Scenario PickRandom()
    {
        int p = _rng.Next(100);
        return p switch
        {
            < 82 => Scenario.Negative,
            < 86 => Scenario.Hpv16Positive,
            < 88 => Scenario.Hpv18Positive,
            < 89 => Scenario.Hpv45Positive,
            < 95 => Scenario.OtherHighRiskPositive,
            < 97 => Scenario.MultipleGenotypes,
            < 99 => Scenario.InvalidControlFailure,
            _ => Scenario.Indeterminate,
        };
    }

    private void SetPos(TargetResult r, double min, double max) { r.Result = ResultValues.Positive; r.Ct = Ct(min, max); }
    private static void SetNeg(TargetResult r) { r.Result = ResultValues.Negative; r.Ct = ""; }
    private string Ct(double min, double max) => (min + _rng.NextDouble() * (max - min)).ToString("0.0", CultureInfo.InvariantCulture);
}
