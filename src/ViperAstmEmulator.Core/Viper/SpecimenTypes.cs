namespace ViperAstmEmulator.Core.Viper;

/// <summary>A specimen type that can be reported in O.16 (specimen descriptor).</summary>
/// <param name="Code">Value sent in the message.</param>
/// <param name="Description">Human readable explanation shown in the dropdown.</param>
public sealed record SpecimenTypeOption(string Code, string Description)
{
    public string Display => $"{Code}  -  {Description}";
}

/// <summary>
/// Specimen types accepted by the BD Onclarity HPV assay on the BD Viper LT.
/// The codes are the emulator's own readable codes; replace them with the codes from the
/// BD LIS interface specification / your LIS mapping where they differ (the field stays editable).
/// </summary>
public static class SpecimenTypes
{
    public static readonly IReadOnlyList<SpecimenTypeOption> Known =
    [
        new("SurePath", "BD SurePath Preservative Fluid (liquid-based cytology)"),
        new("ThinPrep", "Hologic ThinPrep PreservCyt Solution (liquid-based cytology)"),
        new("CervicalBrush", "Cervical brush specimen in BD Onclarity HPV Cervical Brush Diluent"),
        new("SelfVaginal", "Self-collected vaginal specimen (BD Onclarity HPV self-collection)"),
        new("Cervical", "Cervical specimen, type not specified"),
    ];

    public static SpecimenTypeOption? Find(string? code) =>
        Known.FirstOrDefault(t => string.Equals(t.Code, code, StringComparison.OrdinalIgnoreCase));
}
