namespace GrowDiary.Web.Services;

/// <summary>
/// Aus den Maßen eines Zeltes abgeleitete Größen — die einzige Stelle, an der sie gerechnet werden.
/// </summary>
/// <remarks>
/// Eine Wahrheit je Zahl: Volumen, Grundfläche und Luftwechsel standen vorher im Frontend (nur zur Anzeige),
/// und die CO₂-Steuerung führte ein eigenes, von Hand eingetragenes Zeltvolumen. Wer die Maße änderte, änderte
/// das CO₂-Volumen nicht mit. Jetzt tragen Nutzer nur die Maße ein (Einstellungen → Zelte); alles andere kommt von hier.
/// </remarks>
public static class ZeltMasse
{
    /// <summary>Volumen in m³ auf zwei Stellen — oder null, solange ein Maß fehlt oder nicht größer als 0 ist.</summary>
    public static double? VolumenM3(int? breiteCm, int? tiefeCm, int? hoeheCm)
    {
        if (breiteCm is not > 0 || tiefeCm is not > 0 || hoeheCm is not > 0) return null;
        return Math.Round((double)breiteCm.Value * tiefeCm.Value * hoeheCm.Value / 1_000_000d, 2, MidpointRounding.AwayFromZero);
    }

    /// <summary>Grundfläche in m², ungerundet (damit „Fläche je Pflanze“ nicht an einer Rundung hängt) — oder null, solange ein Maß fehlt.</summary>
    public static double? GrundflaecheM2(int? breiteCm, int? tiefeCm)
    {
        if (breiteCm is not > 0 || tiefeCm is not > 0) return null;
        return (double)breiteCm.Value * tiefeCm.Value / 10_000d;
    }

    /// <summary>
    /// Luftwechsel je Stunde: Abluftleistung (Gesamtleistung aller Abluft-Lüfter, Nennwert) geteilt durch das Volumen.
    /// Ein Richtwert — Filter, Rohrlänge und Drosselung sind nicht berücksichtigt.
    /// </summary>
    public static double? LuftwechselProStunde(double? volumenM3, int? abluftM3h)
    {
        if (volumenM3 is not > 0 || abluftM3h is not > 0) return null;
        return Math.Round(abluftM3h.Value / volumenM3.Value, 0, MidpointRounding.AwayFromZero);
    }
}
