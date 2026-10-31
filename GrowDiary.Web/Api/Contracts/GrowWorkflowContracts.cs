using System.ComponentModel.DataAnnotations;

namespace GrowDiary.Web.Api.Contracts;

public sealed record AddbackDefaultsDto(
    int GrowId,
    string GrowName,
    double? SuggestedReservoirLiters,
    double? SuggestedEcIst,
    double? SuggestedEcZiel,
    double? ReservoirLiters,
    double? EcIst,
    double? EcZiel,
    double EcStock);

public sealed class AddbackCalculateRequest
{
    public double? ReservoirLiters { get; set; }

    [Required]
    public double? EcIst { get; set; }

    [Required]
    public double? EcZiel { get; set; }

    [Required]
    public double? EcStock { get; set; }
}

public sealed record AddbackResultDto(
    bool NeedsAddback,
    double? LitersToAdd,
    double? NewReservoirVolume,
    string? ErrorMessage);

public sealed class HarvestUpsertRequest
{
    [Required]
    public string HarvestedAtLocal { get; set; } = DateTime.Today.ToString("yyyy-MM-dd");

    public double? WetWeightG { get; set; }
    public double? DryWeightG { get; set; }
    public int? DryDays { get; set; }
    public string? YieldNotes { get; set; }
    public double? Rating { get; set; }
    public string? FlavorNotes { get; set; }
    public string? EffectNotes { get; set; }
    public string? NugStructure { get; set; }

    /// <summary>
    /// Einzelgewichte je Pflanze als JSON. Die Summe steht weiterhin in
    /// <see cref="WetWeightG"/> und <see cref="DryWeightG"/> — Auswertungen
    /// rechnen mit denen, damit aeltere Ernten ohne Aufschluesselung zaehlen.
    /// </summary>
    public string? PlantWeightsJson { get; set; }

    /// <summary>Das Trockenzelt der Ernte (Zweck Trocknung); leer = Trocknung im Zelt des Grows.</summary>
    public int? DryingTentId { get; set; }
}

public sealed record HarvestDto(
    int GrowId,
    string GrowName,
    string HarvestedAtLocal,
    double? WetWeightG,
    double? DryWeightG,
    int? DryDays,
    string? YieldNotes,
    double? Rating,
    string? FlavorNotes,
    string? EffectNotes,
    string? NugStructure,
    string? PlantWeightsJson,
    int? DryingTentId = null,
    /// <summary>false = noch keine Ernte gespeichert (die Antwort ist dann nur die Vorbelegung).</summary>
    bool Vorhanden = true);

public sealed record GrowActionResultDto(
    GrowDetailDto Grow,
    string Message);
