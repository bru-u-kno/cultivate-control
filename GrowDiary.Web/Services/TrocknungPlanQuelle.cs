using GrowDiary.Web.Infrastructure;
using GrowDiary.Web.Models;
using GrowDiary.Web.Services.Knowledge;
using GrowDiary.Web.Services.Knowledge.Schema;

namespace GrowDiary.Web.Services;

/// <summary>
/// DIE Stelle, die sagt, welche Trocknungs-Etappen für ein Trockenzelt gelten — für die Kacheln, die Steuerung der Geräte und die Seite
/// Plan › Trocknung. Der Plan gehört dem Zelt, nicht dem Düngeprogramm.
/// </summary>
/// <remarks>
/// Reihenfolge: eigener Plan des Zelts → sonst der Standard „langsam trocknen“. <b>Einmalige Übernahme:</b> Hat das Zelt noch keinen
/// eigenen Plan, die trocknende Charge aber ein Programm mit eigenen Etappen (so war es bis cc.298), werden diese Etappen als Plan des
/// Zelts gespeichert — danach ändert ein Wechsel des Düngeprogramms nichts mehr am Trocknungsplan.
/// </remarks>
public sealed class TrocknungPlanQuelle
{
    private readonly KnowledgeBaseLoader _wissen;
    private readonly TrocknungPlanRepository _plaene;

    public TrocknungPlanQuelle(KnowledgeBaseLoader wissen, TrocknungPlanRepository plaene)
    {
        _wissen = wissen;
        _plaene = plaene;
    }

    /// <summary>Die Etappen dieses Zelts. <paramref name="grow"/> ist die trocknende Charge, falls eine da ist (für die einmalige Übernahme).</summary>
    public IReadOnlyList<TrockenEtappe> Etappen(int tentId, GrowRun? grow = null)
    {
        if (_plaene.Get(tentId) is { } eigene) return eigene;

        var programm = grow is null ? null : _wissen.NutrientPrograms.FirstOrDefault(p =>
            string.Equals(p.Id, grow.FeedProgramId, StringComparison.OrdinalIgnoreCase));
        if (TrocknungsPlan.HatEigene(programm))
        {
            var uebernommen = TrocknungsPlan.Bereinigt(programm!.Trocknung!);
            if (TrocknungsPlan.Pruefen(uebernommen) is null)
            {
                _plaene.Set(tentId, uebernommen);
                return uebernommen;
            }
        }

        return TrocknungsPlan.Standard;
    }

    /// <summary>Etappe und Band für einen Trocknungs-Stand — EINE Stelle für Kachel, Karte „Trocknungs-Ziel“ und Steuerung.</summary>
    public (int Geplant, TrocknungsPlan.Stand? Etappe, TrocknungsPlan.Band? Band) Fuer(int tentId, DryingWindow.Stand stand)
    {
        var etappen = Etappen(tentId, stand.Grow);
        var geplant = etappen.Sum(e => Math.Max(1, e.DauerTage));
        var etappe = TrocknungsPlan.EtappeAm(etappen, stand.Tag);
        var band = etappe is null ? (TrocknungsPlan.Band?)null : TrocknungsPlan.BandFuer(etappe.Etappe);
        return (geplant, etappe, band);
    }

    /// <summary>Speichert den Plan des Zelts (die Etappen sind vorher geprüft).</summary>
    public void Speichern(int tentId, IReadOnlyList<TrockenEtappe> etappen) => _plaene.Set(tentId, etappen);
}
