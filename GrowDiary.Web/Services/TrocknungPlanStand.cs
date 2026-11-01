using GrowDiary.Web.Services.Knowledge;

namespace GrowDiary.Web.Services;

/// <summary>Etappe und Band, die für einen Trocknungs-Stand gelten — EINE Stelle für Kachel, Karte „Trocknungs-Ziel“ und die Steuerung (Teil B).</summary>
public static class TrocknungPlanStand
{
    public static (int Geplant, TrocknungsPlan.Stand? Etappe, TrocknungsPlan.Band? Band) Fuer(KnowledgeBaseLoader wissen, DryingWindow.Stand stand)
    {
        var programm = wissen.NutrientPrograms.FirstOrDefault(p =>
            string.Equals(p.Id, stand.Grow.FeedProgramId, StringComparison.OrdinalIgnoreCase));
        var etappen = TrocknungsPlan.Fuer(programm);
        var geplant = etappen.Sum(e => Math.Max(1, e.DauerTage));
        var etappe = TrocknungsPlan.EtappeAm(etappen, stand.Tag);
        var band = etappe is null ? (TrocknungsPlan.Band?)null : TrocknungsPlan.BandFuer(etappe.Etappe);
        return (geplant, etappe, band);
    }
}
