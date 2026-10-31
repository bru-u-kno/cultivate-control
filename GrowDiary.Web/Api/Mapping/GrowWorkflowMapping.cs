using GrowDiary.Web.Api.Contracts;
using GrowDiary.Web.Models;
using GrowDiary.Web.Services;

namespace GrowDiary.Web.Api.Mapping;

public static class GrowWorkflowMapping
{
    public static AddbackResultDto ToDto(this AddbackCalculator.AddbackResult result)
        => new(
            result.NeedsAddback,
            result.LitersToAdd,
            result.NewReservoirVolume,
            result.ErrorMessage);

    public static HarvestDto ToDto(this HarvestEntry entry, string growName)
        => new(
            entry.GrowId,
            growName,
            entry.HarvestedAt.ToString("yyyy-MM-dd"),
            entry.WetWeightG,
            entry.DryWeightG,
            entry.DryDays,
            entry.YieldNotes,
            entry.Rating,
            entry.FlavorNotes,
            entry.EffectNotes,
            entry.NugStructure,
            entry.PlantWeightsJson,
            entry.DryingTentId);

    public static HarvestDto CreateDefaultHarvestDto(int growId, string growName)
        => new(
            growId,
            growName,
            DateTime.Today.ToString("yyyy-MM-dd"),
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            Vorhanden: false);

    public static HarvestEntry ToEntry(this HarvestUpsertRequest request, int growId)
        => new()
        {
            GrowId = growId,
            HarvestedAt = DateTime.Parse(request.HarvestedAtLocal).Date,
            WetWeightG = request.WetWeightG,
            DryWeightG = request.DryWeightG,
            DryDays = request.DryDays,
            YieldNotes = string.IsNullOrWhiteSpace(request.YieldNotes) ? null : request.YieldNotes.Trim(),
            Rating = request.Rating,
            FlavorNotes = string.IsNullOrWhiteSpace(request.FlavorNotes) ? null : request.FlavorNotes.Trim(),
            EffectNotes = string.IsNullOrWhiteSpace(request.EffectNotes) ? null : request.EffectNotes.Trim(),
            NugStructure = string.IsNullOrWhiteSpace(request.NugStructure) ? null : request.NugStructure.Trim(),

            /* Die Einzelgewichte je Pflanze.
               Sie fehlten hier — der Vertrag traegt sie, das Antwort-DTO gibt
               sie zurueck, und der Weg dazwischen warf sie weg. Wer auf der
               Ernteseite je Pflanze wog, bekam HTTP 200 und in derselben
               Antwort `plantWeightsJson: null`. Bei einem Mehrsorten-Grow ist
               das die einzige Angabe, aus der sich der Ertrag je Sorte je
               ableiten liesse. */
            PlantWeightsJson = string.IsNullOrWhiteSpace(request.PlantWeightsJson)
                ? null
                : request.PlantWeightsJson.Trim(),
            DryingTentId = request.DryingTentId
        };
}
