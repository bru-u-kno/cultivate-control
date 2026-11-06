namespace GrowDiary.Web.Services;

/// <summary>
/// Die Zuluft folgt dem Trocknungsplan: Der Fork entscheidet im Trocknungsbetrieb selbst, ob sie ansaugt und auf welcher Stufe.
/// Hier steht nur die Entscheidung — rein, ohne Home Assistant, damit sie ohne Gerät prüfbar bleibt.
/// </summary>
/// <remarks>
/// Die Zuluft saugt Außenluft ins Trockenzelt, wenn das Zelt feuchter ist als der Plan und die Außenluft absolut trockener ist als die
/// Zeltluft. Sie springt knapp <b>vor</b> dem Entfeuchter an (<see cref="VorlaufProzent"/>), damit die kostenlose Außenluft zuerst
/// arbeitet; der Entfeuchter bleibt mit seiner Planobergrenze die Rückfallebene. Zwei Sperren schützen das Zelt: zu kalt im Zelt
/// und Frost draußen — beide mit einem Grad Hysterese, damit der Lüfter an der Grenze nicht flattert.
/// </remarks>
public static class TrocknungZuluft
{
    /// <summary>Die Zuluft springt so viele Prozentpunkte unter dem Einschaltpunkt des Entfeuchters an.</summary>
    public const double VorlaufProzent = 1;

    public const string ZustandKeinMesswert = "keinMesswert";
    public const string ZustandZeltZuKalt = "zeltZuKalt";
    public const string ZustandFrost = "frost";
    public const string ZustandZeltZuTrocken = "zeltZuTrocken";
    public const string ZustandZeltImPlan = "zeltImPlan";
    public const string ZustandAussenFeuchter = "aussenFeuchter";
    public const string ZustandSaugt = "saugt";

    /// <param name="ZeltTempC">Temperatur im Trockenzelt.</param>
    /// <param name="ZeltRhProzent">Luftfeuchte im Trockenzelt.</param>
    /// <param name="AussenAbsolutGm3">Absolute Feuchte der Außenluft in g/m³.</param>
    /// <param name="AussenTempC">Temperatur draußen.</param>
    /// <param name="PlanRhProzent">Planwert der Luftfeuchte (Ziel der heutigen Etappe).</param>
    public sealed record Lage(
        double? ZeltTempC, double? ZeltRhProzent, double? AussenAbsolutGm3, double? AussenTempC, double? PlanRhProzent);

    /// <param name="MindestDifferenzGm3">Ab wie viel Unterschied (Zelt minus außen) das Ansaugen lohnt.</param>
    /// <param name="AbschaltAbstandGm3">Wie weit die Differenz darunter fallen muss, bevor die Zuluft aufhört.</param>
    /// <param name="AbstandUnterPlanProzent">Liegt das Zelt mehr als so viel Prozentpunkte unter dem Planwert, bleibt sie aus.</param>
    /// <param name="StufeMin">Stufe bei knapper Differenz.</param>
    /// <param name="Hoechststufe">Stärker saugt sie im Trocknungsbetrieb nie.</param>
    public sealed record Regel(
        double MindestDifferenzGm3, double AbschaltAbstandGm3, double AbstandUnterPlanProzent,
        double ZeltTempMinC, double AussenTempMinC, int StufeMin, int Hoechststufe);

    /// <param name="Stufe">0 = aus, sonst die Stufe, auf der die Zuluft saugen soll.</param>
    /// <param name="Differenz">Zelt minus außen in g/m³ (positiv: Ansaugen trocknet); <c>null</c>, wenn nicht rechenbar.</param>
    /// <param name="ZeltAbsolutGm3">Absolute Feuchte der Zeltluft; <c>null</c>, wenn nicht rechenbar.</param>
    public sealed record Entscheidung(int Stufe, string Zustand, double? Differenz, double? ZeltAbsolutGm3)
    {
        public bool Saugt => Stufe > 0;
    }

    /// <summary>Absolute Feuchte in g/m³ nach der Magnus-Formel — dieselbe wie die Rechenwerte „Absolute Feuchte“ in Home Assistant.</summary>
    public static double? Absolut(double? tempC, double? rhProzent)
    {
        if (tempC is not { } t || rhProzent is not { } rh || !double.IsFinite(t) || !double.IsFinite(rh)) return null;
        return Math.Round(216.7 * (rh / 100 * 6.112 * Math.Exp(17.62 * t / (243.12 + t))) / (t + 273.15), 2);
    }

    /// <param name="saugteBisher">Ob die Zuluft in der Minute davor lief (Haltebereich der Hysterese).</param>
    public static Entscheidung Entscheide(Lage lage, Regel regel, bool saugteBisher)
    {
        var zeltAbs = Absolut(lage.ZeltTempC, lage.ZeltRhProzent);
        double? diff = zeltAbs is { } z && lage.AussenAbsolutGm3 is { } a && double.IsFinite(a) ? Math.Round(z - a, 2) : null;

        // Ohne Messwerte bleibt die Zuluft aus: kein plötzliches Ansaugen auf Verdacht, in einem Zelt, das man nicht lesen kann.
        if (lage.ZeltTempC is not { } zeltTemp || !double.IsFinite(zeltTemp)
            || lage.ZeltRhProzent is not { } rh || !double.IsFinite(rh)
            || lage.PlanRhProzent is not { } plan || !double.IsFinite(plan)
            || diff is not { } d
            || lage.AussenTempC is not { } aussenTemp || !double.IsFinite(aussenTemp))
            return new Entscheidung(0, ZustandKeinMesswert, diff, zeltAbs);

        // Schutz vor dem Auskühlen: darunter aus, wieder frei ab Grenze + 1 °C (wie die Regelung im Keller).
        if (saugteBisher ? zeltTemp < regel.ZeltTempMinC : zeltTemp < regel.ZeltTempMinC + 1)
            return new Entscheidung(0, ZustandZeltZuKalt, d, zeltAbs);

        // Frostschutz draußen: darunter aus, wieder frei ab Grenze + 1 °C.
        if (saugteBisher ? aussenTemp < regel.AussenTempMinC : aussenTemp < regel.AussenTempMinC + 1)
            return new Entscheidung(0, ZustandFrost, d, zeltAbs);

        // Beim Trocknen ist zu schnell ebenso schlecht wie zu feucht: Zu weit unter dem Planwert bleibt die Zuluft aus.
        var einAb = plan - VorlaufProzent;
        if (rh < plan - regel.AbstandUnterPlanProzent)
            return new Entscheidung(0, ZustandZeltZuTrocken, d, zeltAbs);

        // Ein: Zelt auf/über dem Einschaltpunkt UND die Außenluft ist trockener. Im Haltebereich bleibt sie, wie sie ist.
        var feuchtGenug = saugteBisher ? true : rh >= einAb;
        if (!feuchtGenug) return new Entscheidung(0, ZustandZeltImPlan, d, zeltAbs);

        var genugUnterschied = saugteBisher
            ? d >= regel.MindestDifferenzGm3 - regel.AbschaltAbstandGm3
            : d >= regel.MindestDifferenzGm3;
        if (!genugUnterschied) return new Entscheidung(0, ZustandAussenFeuchter, d, zeltAbs);

        // Im Haltebereich unter dem Einschaltpunkt läuft sie weiter, solange das Zelt nicht unter den Planwert minus Abstand fällt.
        return new Entscheidung(StufeFuer(d, regel), ZustandSaugt, d, zeltAbs);
    }

    /// <summary>Die Stufe wächst mit der Differenz von Stufe min bis zur Höchststufe (über 3 g/m³ über der Mindest-Differenz ist sie voll).</summary>
    public static int StufeFuer(double differenz, Regel regel)
    {
        var hoch = Math.Clamp(regel.Hoechststufe, 1, 10);
        var tief = Math.Clamp(regel.StufeMin, 1, hoch);
        var f = Math.Clamp((differenz - regel.MindestDifferenzGm3) / 3.0, 0, 1);
        return (int)Math.Round(tief + (hoch - tief) * f, MidpointRounding.AwayFromZero);
    }
}
