using GrowDiary.Web.Models;

namespace GrowDiary.Web.Services;

/// <summary>Was ein Schaltbefehl bewirkt hat.</summary>
public enum Schaltausgang
{
    /// <summary>Das Gerät meldet den neuen Zustand zurück (oder Home Assistant hat bestätigt).</summary>
    Bestaetigt,

    /// <summary>Gesendet, aber noch nicht zurückgemeldet — ob es wirkte, sagt erst ein späterer Blick.</summary>
    Unbestaetigt,

    /// <summary>Home Assistant hat den Befehl nicht angenommen: nichts wurde geschaltet.</summary>
    Abgelehnt,
}

/// <summary>
/// Fork AI (cc.277): Alles, was der Ablauf von „Zelt ruhend" von der Anlage braucht — als Schnittstelle, damit sich der Ablauf
/// ohne Home Assistant prüfen lässt (Teilfehler, Abbruch, Reihenfolge).
/// </summary>
public interface IZeltRuheWerkzeug
{
    /// <summary>Der Zustand einer Entität, so wie Home Assistant ihn meldet; <c>null</c>, wenn sie nicht antwortet.</summary>
    Task<string?> ZustandAsync(string entity, CancellationToken ct);

    /// <summary>Den Dienstaufruf ausführen. <paramref name="soll"/> ist der Zustand, den ein Auswahlfeld danach melden muss.</summary>
    Task<Schaltausgang> SchaltenAsync(
        string entity, (string Domain, string Dienst, IReadOnlyDictionary<string, object>? Daten) aktion, string? soll, CancellationToken ct);

    /// <summary>Automatik des Geräts aus. <c>Ok</c> falsch heißt: nichts hat sich geändert (der alte Stand ist wiederhergestellt). <c>WarAn</c> null: keine Automatik.</summary>
    Task<(bool Ok, bool? WarAn)> AutomatikAusAsync(string kennung, CancellationToken ct);

    Task<bool> AutomatikAnAsync(string kennung, CancellationToken ct);

    /// <summary>Schaltet die Fork-eigene Kühler-Regelung aus und liefert die Zelte, bei denen sie an war.</summary>
    IReadOnlyList<int> KuehlerReglerAus();

    void KuehlerReglerAn(IEnumerable<int> zeltIds);

    void WaechterPausierenBis(DateTime bisUtc);

    void WaechterFortsetzen();
}

/// <summary>
/// Fork AI (cc.277): Der Ablauf von „Zelt ruhend" und „Zelt wieder aktiv".
/// </summary>
/// <remarks>
/// <para><b>Zuerst merken, dann schalten.</b> Vor jedem Eingriff steht der Merker im Speicher (<c>Ruhend</c> ist dann schon
/// wahr), und nach jedem Gerät wird gespeichert. Bricht die Anfrage mitten drin ab, weiß der Fork danach, was er angefasst hat —
/// „Zelt wieder aktiv" führt zurück. Der Dienst ruft diesen Ablauf deshalb ohne das Token der Anfrage auf.</para>
/// <para><b>Je Gerät: Automatik, Fork-Regler, dann das Gerät.</b> Scheitert schon die Automatik, ist der alte Stand
/// wiederhergestellt und das Gerät unberührt; es bleibt kein Merker zurück, der etwas Falsches versprechen würde.</para>
/// <para><b>Beim Wecken führt die Regelung.</b> Hat ein Gerät eine Automatik (oder den Fork-Regler), kommt nur diese zurück —
/// sie schaltet das Gerät mit ihren Schutzzeiten selbst. Direkt zurückgestellt werden nur Geräte ohne Regelung (Licht, Abluft)
/// und solche, deren Automatik schon vorher aus war. Was nicht gelingt, bleibt gemerkt, und ein zweiter Versuch holt es nach.</para>
/// </remarks>
public sealed class ZeltRuheAblauf
{
    private readonly IZeltRuheWerkzeug _w;
    private readonly Func<ZeltRuheEinstellungen> _laden;
    private readonly Action<ZeltRuheEinstellungen> _speichern;
    private readonly ILogger _logger;
    private readonly Func<DateTime> _jetztUtc;

    public ZeltRuheAblauf(
        IZeltRuheWerkzeug werkzeug, Func<ZeltRuheEinstellungen> laden, Action<ZeltRuheEinstellungen> speichern,
        ILogger logger, Func<DateTime>? jetztUtc = null)
    {
        _w = werkzeug;
        _laden = laden;
        _speichern = speichern;
        _logger = logger;
        _jetztUtc = jetztUtc ?? (() => DateTime.UtcNow);
    }

    // ------------------------------------------------------------------ Ruhend

    /// <param name="geraete">Die Geräte dieses Durchgangs mit ihrer Entität — nur Geräte, die es gibt.</param>
    public async Task<IReadOnlyList<ZeltRuheSchritt>> RuhendAsync(IReadOnlyList<(ZeltRuheArt Art, string Entity)> geraete, CancellationToken ct)
    {
        var e = _laden();
        var schritte = new List<ZeltRuheSchritt>();
        foreach (var (art, entity) in geraete)
            schritte.Add(await GeraetAusAsync(e, art, entity, ct));

        // Ohne Wächter-Pause meldete der Licht-Wächter jede geplante Hellzeit als Abweichung.
        if (e.Gemerkt.Any(m => m.Kennung == ZeltRuheArten.Licht) && !e.WaechterAngehalten)
        {
            _w.WaechterPausierenBis(_jetztUtc() + ZeltRuheService.WaechterPause);
            e.WaechterAngehalten = true;
        }
        Abschliessen(e);
        return schritte;
    }

    private async Task<ZeltRuheSchritt> GeraetAusAsync(ZeltRuheEinstellungen e, ZeltRuheArt art, string entity, CancellationToken ct)
    {
        ZeltRuheSchritt Fehl(string text) => new(art.Kennung, art.Titel, false, text);

        var vorher = await _w.ZustandAsync(entity, ct);
        var (_, laeuft) = ZeltRuheService.Beschreiben(SteuerungGeraeteService.Domain(entity), vorher);
        if (laeuft is null) return Fehl("antwortet nicht — nicht ausgeschaltet");

        var aktion = ZeltRuheService.Ausschalten(entity);
        if (aktion is null) return Fehl("diese Art Gerät kann „Zelt ruhend“ nicht ausschalten");

        // 1. Zuerst merken, dann eingreifen.
        var merker = new ZeltRuheMerker { Kennung = art.Kennung, Entity = entity, Vorher = vorher };
        e.Gemerkt.RemoveAll(m => string.Equals(m.Kennung, art.Kennung, StringComparison.OrdinalIgnoreCase));
        e.Gemerkt.Add(merker);
        e.Ruhend = true;
        e.SeitUtc ??= _jetztUtc();
        _speichern(e);

        // 2. Automatik aus. Gelingt das nicht, ist der alte Stand zurück und das Gerät unberührt.
        (bool Ok, bool? WarAn) automatik;
        try { automatik = await _w.AutomatikAusAsync(art.Kennung, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Zelt ruhend: Automatik von {Kennung} nicht ausgeschaltet.", art.Kennung);
            automatik = (false, null);
        }
        if (!automatik.Ok)
        {
            Vergessen(e, merker);
            return Fehl("Automatik ließ sich nicht ausschalten — Gerät unberührt");
        }
        merker.AutomatikWarAn = automatik.WarAn;

        // 3. Der Fork-eigene Kühler-Regler schaltet die Steckdose sonst binnen einer Minute wieder ein.
        if (art.Kennung == ZeltRuheArten.Chiller) merker.ChillerZelte = _w.KuehlerReglerAus().ToList();
        _speichern(e);

        // 4. Das Gerät selbst.
        var (domain, dienst, daten) = aktion.Value;
        Schaltausgang ausgang;
        try { ausgang = await _w.SchaltenAsync(entity, (domain, dienst, daten), AcModi.Aus, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Zelt ruhend: {Kennung} nicht ausgeschaltet.", art.Kennung);
            ausgang = Schaltausgang.Abgelehnt;
        }

        switch (ausgang)
        {
            case Schaltausgang.Bestaetigt:
                _logger.LogInformation("Zelt ruhend: {Kennung} ausgeschaltet.", art.Kennung);
                return new ZeltRuheSchritt(art.Kennung, art.Titel, true, merker.Gefuehrt ? "Automatik und Gerät aus" : "ausgeschaltet");
            case Schaltausgang.Unbestaetigt:
                // Gesendet, noch nicht zurückgemeldet: gemerkt bleiben, damit „wieder aktiv" es zurückstellt.
                return Fehl("gesendet, aber vom Gerät noch nicht bestätigt — bitte nachsehen");
            default:
                // Hat sich außer der Regelung nichts geändert, gibt es nichts zurückzustellen.
                if (!merker.Gefuehrt) { Vergessen(e, merker); return Fehl("Gerät hat den Befehl nicht angenommen"); }
                return Fehl("Gerät hat den Befehl nicht angenommen — nur die Regelung ist aus");
        }
    }

    // ------------------------------------------------------------------ Wieder aktiv

    public async Task<IReadOnlyList<ZeltRuheSchritt>> AktivAsync(CancellationToken ct)
    {
        var e = _laden();
        var schritte = new List<ZeltRuheSchritt>();
        foreach (var merker in e.Gemerkt.ToList())
        {
            var art = ZeltRuheArten.Finden(merker.Kennung);
            var titel = art?.Titel ?? merker.Kennung;
            var (ok, text) = await GeraetAktivAsync(merker, ct);
            schritte.Add(new ZeltRuheSchritt(merker.Kennung, titel, ok, text));
            // Sofort festhalten: was wieder steht, wird nicht noch einmal angefasst.
            if (ok) { Vergessen(e, merker); }
        }

        // Die Wächter-Pause erst aufheben, wenn die Lampe wirklich wieder in ihrem Zustand ist.
        if (e.WaechterAngehalten && !e.Gemerkt.Any(m => m.Kennung == ZeltRuheArten.Licht))
        {
            _w.WaechterFortsetzen();
            e.WaechterAngehalten = false;
        }
        Abschliessen(e);
        return schritte;
    }

    private async Task<(bool Ok, string Text)> GeraetAktivAsync(ZeltRuheMerker merker, CancellationToken ct)
    {
        try
        {
            // Führt eine Regelung das Gerät, kommt nur sie zurück — sie schaltet mit ihren Schutzzeiten selbst. Direkt
            // zurückgestellt wird nur, was keine Regelung hat oder dessen Automatik schon vorher aus war.
            if (!merker.Gefuehrt && merker.Entity is not null && ZeltRuheService.Wiederherstellen(merker.Entity, merker.Vorher) is { } aktion)
            {
                var ausgang = await _w.SchaltenAsync(merker.Entity, aktion, merker.Vorher, ct);
                if (ausgang == Schaltausgang.Unbestaetigt) return (false, "gesendet, aber vom Gerät noch nicht bestätigt");
                if (ausgang != Schaltausgang.Bestaetigt) return (false, "Gerät hat den Befehl nicht angenommen");
            }

            if (merker.ChillerZelte.Count > 0) _w.KuehlerReglerAn(merker.ChillerZelte);

            if (merker.AutomatikWarAn == true && !await _w.AutomatikAnAsync(merker.Kennung, ct))
                return (false, "Automatik ließ sich nicht einschalten");

            return (true, merker.Gefuehrt ? "Regelung wieder an" : "wieder wie vorher");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Zelt wieder aktiv: {Kennung} nicht wiederhergestellt.", merker.Kennung);
            return (false, "ließ sich nicht wiederherstellen");
        }
    }

    // ------------------------------------------------------------------ Hilfen

    private void Vergessen(ZeltRuheEinstellungen e, ZeltRuheMerker merker)
    {
        e.Gemerkt.Remove(merker);
        _speichern(e);
    }

    private void Abschliessen(ZeltRuheEinstellungen e)
    {
        e.Ruhend = e.Gemerkt.Count > 0;
        if (!e.Ruhend) { e.SeitUtc = null; e.WaechterAngehalten = false; }
        _speichern(e);
    }
}
