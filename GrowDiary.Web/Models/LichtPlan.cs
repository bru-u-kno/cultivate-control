namespace GrowDiary.Web.Models;

/// <summary>Ein gemessener Punkt der Lampe: Bei dieser Stufe kommen so viele µmol/m²s an.</summary>
public sealed class LichtMesspunkt
{
    public int Stufe { get; set; }
    public double Ppfd { get; set; }
}

/// <summary>
/// Fork AI: Die Kalibrierung der Lichtleistung und die Automatik „Stufe nach Plan".
/// </summary>
/// <remarks>
/// Der Plan nennt je Woche einen Lichtwert (PPFD), die Lampe kennt nur Stufen 1 bis 10. Die Kalibrierung
/// übersetzt das: die Herstellerangabe bei Stufe 10 und eigene Messpunkte ergeben eine Kurve.
/// Die Automatik ist aus, bis jemand sie einschaltet (Voreinstellung).
/// </remarks>
public sealed class LichtKalibrierung
{
    /// <summary>Herstellerangabe: PPFD bei der höchsten Stufe (10) im angegebenen Abstand.</summary>
    public double? HerstellerPpfd { get; set; }

    /// <summary>Abstand der Herstellerangabe in cm — nur zur Anzeige.</summary>
    public double? HerstellerAbstandCm { get; set; }

    public List<LichtMesspunkt> Messpunkte { get; set; } = [];

    /// <summary>Die Stufe beim Wochenwechsel von selbst nach dem Plan setzen. Voreinstellung: aus.</summary>
    public bool AutomatikStufe { get; set; }

    /// <summary>Die Stufe, die die Automatik zuletzt gesetzt hat — daran erkennt sie eine Handstellung.</summary>
    public int? AutoStufe { get; set; }

    /// <summary>Die Planwoche (Spalten-Kennung), für die die Automatik zuletzt gesetzt hat.</summary>
    public string? AutoWoche { get; set; }

    /// <summary>Handstellung entdeckt: Die Automatik schweigt, bis die Planwoche wechselt.</summary>
    public string? HandBisWoche { get; set; }
}

/// <summary>Ein Punkt der Kurve für die Anzeige.</summary>
/// <param name="Herkunft">gemessen / Herstellerangabe / Rechnung</param>
public sealed record LichtKurvenPunkt(int Stufe, double Ppfd, string Herkunft);

/// <summary>Eine Planwoche mit Lichtwert und der Stufe, die dazu passt.</summary>
public sealed record LichtPlanWoche(string Id, string Label, string Phase, double? Ppfd, int? VorschlagStufe, double? Erreicht, bool Jetzt);

/// <summary>Die Seite „Lichtplan" für die Oberfläche.</summary>
/// <param name="Phase">veggie / bluete aus dem Grow-Plan — null, wenn gerade kein Grow läuft.</param>
/// <param name="AutomatikHinweis">Ein Satz, was die Automatik gerade tut oder warum nicht.</param>
public sealed record LichtPlanStand(
    LichtKalibrierung Kalibrierung,
    bool Kalibriert,
    IReadOnlyList<LichtKurvenPunkt> Kurve,
    string? Programm,
    string? Phase,
    IReadOnlyList<LichtPlanWoche> Wochen,
    string AutomatikHinweis);

/// <summary>Die Kurve Stufe → PPFD. Rein, ohne Zugriff auf Geräte oder Datenbank.</summary>
public static class LichtKennlinie
{
    public const string Gemessen = "gemessen";
    public const string Hersteller = "Herstellerangabe";
    public const string Rechnung = "Rechnung";

    /// <summary>Stützpunkte: Messpunkte, dazu die Herstellerangabe bei Stufe 10, wenn dort nicht gemessen wurde.</summary>
    private static List<(int Stufe, double Ppfd, string Herkunft)> Anker(LichtKalibrierung k)
    {
        var anker = k.Messpunkte
            .Where(m => m.Stufe is >= 1 and <= 10 && m.Ppfd > 0)
            .GroupBy(m => m.Stufe)
            .Select(g => (g.Key, g.Last().Ppfd, Gemessen))
            .ToList();
        if (k.HerstellerPpfd is > 0 && anker.All(a => a.Key != 10))
            anker.Add((10, k.HerstellerPpfd.Value, Hersteller));
        return anker.OrderBy(a => a.Key).ToList();
    }

    /// <summary>Die zehn Punkte der Kurve — leer, solange es weder Messung noch Herstellerangabe gibt.</summary>
    public static IReadOnlyList<LichtKurvenPunkt> Kurve(LichtKalibrierung k)
    {
        var anker = Anker(k);
        if (anker.Count == 0) return [];

        // Der Ursprung (Stufe 0 = kein Licht) hilft nur beim Verbinden, er steht nicht in der Anzeige.
        var linie = new List<(double X, double Y)> { (0, 0) };
        linie.AddRange(anker.Select(a => ((double)a.Stufe, a.Ppfd)));

        var kurve = new List<LichtKurvenPunkt>();
        for (var stufe = 1; stufe <= 10; stufe++)
        {
            var fest = anker.FirstOrDefault(a => a.Stufe == stufe);
            kurve.Add(fest.Herkunft is not null
                ? new LichtKurvenPunkt(stufe, Math.Round(fest.Ppfd), fest.Herkunft)
                : new LichtKurvenPunkt(stufe, Math.Round(Verbinden(linie, stufe)), Rechnung));
        }

        return kurve;
    }

    private static double Verbinden(List<(double X, double Y)> linie, double x)
    {
        // Zwischen zwei Stützpunkten gerade verbinden; über dem letzten mit der Steigung des letzten Stücks weiter.
        for (var i = 1; i < linie.Count; i++)
        {
            if (x > linie[i].X && i < linie.Count - 1) continue;
            var (x0, y0) = linie[i - 1];
            var (x1, y1) = linie[i];
            return Math.Max(0, y0 + (y1 - y0) * (x - x0) / (x1 - x0));
        }

        return 0;
    }

    /// <summary>Die Stufe, deren PPFD dem Wunsch am nächsten kommt (bei Gleichstand die niedrigere).</summary>
    public static int? StufeFuer(IReadOnlyList<LichtKurvenPunkt> kurve, double ppfd)
        => kurve.Count == 0 ? null : kurve.OrderBy(p => Math.Abs(p.Ppfd - ppfd)).ThenBy(p => p.Stufe).First().Stufe;

    public static Dictionary<string, string> Pruefen(LichtKalibrierung k)
    {
        var fehler = new Dictionary<string, string>();
        if (k.HerstellerPpfd is { } h && h is < 1 or > 3000)
            fehler[nameof(k.HerstellerPpfd)] = "Wert zwischen 1 und 3000 µmol/m²s.";
        if (k.HerstellerAbstandCm is { } a && a is < 1 or > 300)
            fehler[nameof(k.HerstellerAbstandCm)] = "Abstand zwischen 1 und 300 cm.";
        if (k.Messpunkte.Count > 10)
            fehler[nameof(k.Messpunkte)] = "Höchstens zehn Messpunkte.";
        else if (k.Messpunkte.Any(m => m.Stufe is < 1 or > 10 || m.Ppfd is < 1 or > 3000))
            fehler[nameof(k.Messpunkte)] = "Stufe 1 bis 10, Wert zwischen 1 und 3000 µmol/m²s.";
        else if (k.Messpunkte.GroupBy(m => m.Stufe).Any(g => g.Count() > 1))
            fehler[nameof(k.Messpunkte)] = "Jede Stufe nur einmal messen.";
        return fehler;
    }
}
