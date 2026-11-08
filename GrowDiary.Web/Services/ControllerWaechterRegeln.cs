using GrowDiary.Web.Models;

namespace GrowDiary.Web.Services;

/// <summary>Was der Wächter am Controller sieht.</summary>
public enum ControllerBefund
{
    /// <summary>Verbunden, und die Feuchte bewegt sich.</summary>
    Gesund,

    /// <summary>Die Verbindungs-Entität meldet „aus“ oder „nicht verfügbar“.</summary>
    Offline,

    /// <summary>Die Verbindung zeigt „an“, aber der Feuchtewert hat sich lange nicht aktualisiert.</summary>
    Eingefroren,

    /// <summary>Home Assistant war nicht erreichbar oder nichts ist zugeordnet — dann wird nichts geschaltet.</summary>
    NichtPruefbar,
}

/// <summary>Die Lage eines Controllers in diesem Durchgang.</summary>
/// <param name="Befund">Was gesehen wurde.</param>
/// <param name="SeitUtc">Seit wann der Zustand besteht (letzter Zustandswechsel bzw. letzte Aktualisierung).</param>
/// <param name="SteckdoseSchaltbar">Eine Steckdose ist gewählt und meldet „an“ — nur dann wird neu gestartet.</param>
public sealed record ControllerLage(ControllerBefund Befund, DateTime? SeitUtc, bool SteckdoseSchaltbar);

/// <summary>Was der Wächter sich über einen Ausfall merkt — liegt in der Datenbank, damit ein Neustart der App nichts vergisst.</summary>
public sealed class ControllerMerker
{
    /// <summary>Seit wann der laufende Ausfall besteht (der erste gesehene Zeitpunkt).</summary>
    public DateTime? AusfallSeitUtc { get; set; }

    public bool Gemeldet { get; set; }

    public bool KeineHilfeGemeldet { get; set; }

    public bool LimitGemeldet { get; set; }

    /// <summary>Neustarts in diesem Ausfall.</summary>
    public int NeustartsImAusfall { get; set; }

    /// <summary>Alle Neustarts der letzten 24 Stunden — zählt gegen die Tagesgrenze.</summary>
    public List<DateTime> NeustartsUtc { get; set; } = new();
}

public enum ControllerAktion
{
    MeldenAusfall,
    Neustarten,
    MeldenKeineHilfe,
    MeldenLimit,
    MeldenWieder,
}

/// <summary>
/// Die Entscheidungen des Controller-Wächters als reine Funktionen: Lage, Merker und Uhr rein, neue Aktionen raus.
/// Der Dienst führt sie aus und legt den Merker ab.
/// </summary>
public static class ControllerWaechterRegeln
{
    /// <summary>Ein Feuchtewert, der so lange nicht aktualisiert wurde, gilt als eingefroren.</summary>
    /// <remarks>Gemessen am 08.11.2026: der Controller liefert alle 10 Sekunden einen Wert.</remarks>
    public static readonly TimeSpan EingefrorenNach = TimeSpan.FromMinutes(15);

    /// <summary>So lange nach einem Neustart wird abgewartet, bevor er als wirkungslos gilt.</summary>
    public static readonly TimeSpan Kontrolle = TimeSpan.FromMinutes(5);

    /// <summary>Nach einem wirkungslosen Neustart herrscht mindestens so lange Ruhe.</summary>
    public static readonly TimeSpan Ruhe = TimeSpan.FromMinutes(60);

    public static readonly TimeSpan Tag = TimeSpan.FromHours(24);

    /// <summary>
    /// Beurteilt Verbindung und Feuchtefühler. <paramref name="statusZugeordnet"/>/<paramref name="feuchteZugeordnet"/>
    /// sagen, ob die Rolle belegt ist; ein belegter Status, der „aus“ oder „unavailable“ meldet, ist ein Ausfall.
    /// </summary>
    public static (ControllerBefund Befund, DateTime? SeitUtc) Beurteilen(
        bool haErreichbar,
        string? status, DateTime? statusGewechseltUtc,
        string? feuchte, DateTime? feuchteAktualisiertUtc,
        DateTime jetztUtc)
    {
        if (!haErreichbar) return (ControllerBefund.NichtPruefbar, null);

        if (status is not null)
        {
            if (IstOffline(status)) return (ControllerBefund.Offline, statusGewechseltUtc ?? jetztUtc);
        }

        if (feuchte is not null)
        {
            // Eine nicht verfügbare Feuchte bei verbundenem Controller: derselbe Ausfall, nur von der anderen Seite.
            if (IstOffline(feuchte)) return (ControllerBefund.Offline, feuchteAktualisiertUtc ?? jetztUtc);
            if (feuchteAktualisiertUtc is { } stand && jetztUtc - stand >= EingefrorenNach)
                return (ControllerBefund.Eingefroren, stand);
        }

        if (status is null && feuchte is null) return (ControllerBefund.NichtPruefbar, null);
        return (ControllerBefund.Gesund, null);
    }

    public static bool IstOffline(string zustand)
        => zustand.Equals("off", StringComparison.OrdinalIgnoreCase)
           || zustand.Equals("unavailable", StringComparison.OrdinalIgnoreCase)
           || zustand.Equals("unknown", StringComparison.OrdinalIgnoreCase);

    /// <summary>Was in diesem Durchgang zu tun ist. Verändert den <paramref name="merker"/> (er wird danach abgelegt).</summary>
    public static IReadOnlyList<ControllerAktion> Entscheiden(
        ControllerWaechterEintrag e, ControllerLage lage, ControllerMerker merker, DateTime jetztUtc)
    {
        var aktionen = new List<ControllerAktion>();
        merker.NeustartsUtc.RemoveAll(t => jetztUtc - t >= Tag);

        if (lage.Befund == ControllerBefund.NichtPruefbar) return aktionen;

        if (lage.Befund == ControllerBefund.Gesund)
        {
            if (merker.Gemeldet || merker.NeustartsImAusfall > 0) aktionen.Add(ControllerAktion.MeldenWieder);
            merker.AusfallSeitUtc = null;
            merker.Gemeldet = merker.KeineHilfeGemeldet = merker.LimitGemeldet = false;
            merker.NeustartsImAusfall = 0;
            return aktionen;
        }

        merker.AusfallSeitUtc ??= lage.SeitUtc ?? jetztUtc;
        var dauer = jetztUtc - merker.AusfallSeitUtc.Value;

        if (!merker.Gemeldet && dauer >= TimeSpan.FromMinutes(e.MeldenNachMin))
        {
            merker.Gemeldet = true;
            aktionen.Add(ControllerAktion.MeldenAusfall);
        }

        if (e.Modus != ControllerWaechterEintrag.Neustarten || !lage.SteckdoseSchaltbar
            || dauer < TimeSpan.FromMinutes(e.NeustartNachMin)) return aktionen;

        var letzter = merker.NeustartsUtc.Count > 0 ? merker.NeustartsUtc.Max() : (DateTime?)null;
        if (letzter is { } l)
        {
            if (jetztUtc - l < Kontrolle) return aktionen;
            if (merker.NeustartsImAusfall > 0 && !merker.KeineHilfeGemeldet)
            {
                merker.KeineHilfeGemeldet = true;
                aktionen.Add(ControllerAktion.MeldenKeineHilfe);
            }
        }

        if (merker.NeustartsUtc.Count >= e.MaxNeustartsProTag)
        {
            if (!merker.LimitGemeldet)
            {
                merker.LimitGemeldet = true;
                aktionen.Add(ControllerAktion.MeldenLimit);
            }
            return aktionen;
        }

        if (letzter is { } l2 && jetztUtc - l2 < Ruhe) return aktionen;

        merker.NeustartsUtc.Add(jetztUtc);
        merker.NeustartsImAusfall++;
        merker.KeineHilfeGemeldet = false;
        aktionen.Add(ControllerAktion.Neustarten);
        return aktionen;
    }

    /// <summary>Dauer als Klartext: „3 Std. 48 Min.“, „25 Min.“.</summary>
    public static string Dauer(TimeSpan d)
    {
        var min = (int)Math.Max(0, Math.Round(d.TotalMinutes));
        return min >= 60 ? $"{min / 60} Std. {min % 60} Min." : $"{min} Min.";
    }
}
