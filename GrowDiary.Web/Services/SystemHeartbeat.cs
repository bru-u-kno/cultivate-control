using System.Globalization;
namespace GrowDiary.Web.Services;

/// <summary>
/// The pulse of the background machinery, so the watchdog can tell "everything is quiet
/// because all is well" from "everything is quiet because I stopped working". Held in
/// memory on purpose: after a restart the slate is clean and the next check re-evaluates.
/// </summary>
public sealed class SystemHeartbeat
{
    private readonly object _gate = new();

    /// <summary>
    /// Wann dieser Prozess gestartet ist.
    /// </summary>
    /// <remarks>
    /// Nach einem Neustart ist noch keine Runde gedreht. Ohne diesen Zeitpunkt
    /// las der Watchdog das als „Überwachung steht" und schlug Alarm — jedes
    /// Mal, wenn jemand das Add-on neu startet oder ein Update einspielt.
    /// </remarks>
    public DateTime StartedAtUtc { get; } = DateTime.UtcNow;

    private DateTime? _lastSnapshotRunUtc;
    private DateTime? _lastHaSuccessUtc;
    private string? _lastHaError;
    private string? _notifiedCode;

    /// <summary>Set after each completed snapshot-worker loop.</summary>
    public void MarkSnapshotRun(DateTime nowUtc)
    {
        lock (_gate) { _lastSnapshotRunUtc = nowUtc; }
    }

    /// <summary>Set whenever Home Assistant answered successfully.</summary>
    public void MarkHomeAssistantSuccess(DateTime nowUtc)
    {
        lock (_gate) { _lastHaSuccessUtc = nowUtc; _lastHaError = null; }
    }

    /// <summary>Set when a Home Assistant call failed, with a short reason.</summary>
    public void MarkHomeAssistantFailure(string reason)
    {
        lock (_gate) { _lastHaError = reason; }
    }

    public (DateTime? SnapshotRun, DateTime? HaSuccess, string? HaError) Read()
    {
        lock (_gate) { return (_lastSnapshotRunUtc, _lastHaSuccessUtc, _lastHaError); }
    }

    private DateTime? _haUnreachableSinceUtc;

    /// <summary>
    /// Der Alarm-Wächter hat Home Assistant im Minutentakt nicht erreicht (Verbindung, nicht ein einzelner Sensor).
    /// Merkt nur den ersten Zeitpunkt der Lücke.
    /// </summary>
    public void MarkHaUnreachable(DateTime nowUtc)
    {
        lock (_gate) { _haUnreachableSinceUtc ??= nowUtc; }
    }

    /// <summary>
    /// Home Assistant antwortet wieder. Gibt die Länge der Lücke zurück, wenn vorher eine bestand, sonst null
    /// — und schließt sie damit ab (jede Lücke wird genau einmal gemeldet).
    /// </summary>
    public TimeSpan? MarkHaReachable(DateTime nowUtc)
    {
        lock (_gate)
        {
            if (_haUnreachableSinceUtc is not { } seit) return null;
            _haUnreachableSinceUtc = null;
            return nowUtc - seit;
        }
    }

    /// <summary>The problem code the user was last told about (null = last told all is well).</summary>
    public string? NotifiedCode
    {
        get { lock (_gate) { return _notifiedCode; } }
        set { lock (_gate) { _notifiedCode = value; } }
    }

    /// <summary>Gemeldete Lagen, je Zelt UND Bereich (Schluessel „7:chiller").</summary>
    private readonly Dictionary<string, string> _meldungen = [];

    /// <summary>
    /// Welche Pumpen-Lage dem Betreiber je Zelt zuletzt gemeldet wurde.
    /// </summary>
    /// <remarks>
    /// Je Zelt getrennt, aus demselben Grund, aus dem der Watchdog die dunklen
    /// Zelte im Schlüssel führt: fällt im zweiten Zelt auch die Pumpe aus, ist
    /// das eine neue Lage und verdient eine eigene Nachricht, statt sich hinter
    /// der alten zu verstecken. Nur im Speicher — nach einem Neustart wird neu
    /// bewertet und im Zweifel einmal zu viel gewarnt.
    /// </remarks>
    /// <summary>
    /// Was dem Betreiber je Zelt und BEREICH zuletzt gemeldet wurde.
    /// </summary>
    /// <param name="tentId">Das Zelt.</param>
    /// <param name="bereich">
    /// „pumpe", „chiller", „ups-status", „ups-battery" — jeder Bereich hat
    /// seine eigene Entprellung.
    /// </param>
    /// <remarks>
    /// <para><b>Der Anlass (01.09.2026).</b> Hier stand EINE Merkstelle je
    /// Zelt, und zwei Zweige teilten sie sich: der Pumpen-Wächter schrieb und
    /// las sie, der Kühler-/USV-Zweig las sie nur. Dessen Entprellung konnte
    /// deshalb nie greifen — und hätte er geschrieben, wäre die des
    /// Pumpen-Wächters weg gewesen.</para>
    ///
    /// <para>Zwei Sachen, die nichts miteinander zu tun haben, in einem Fach:
    /// ein stehender Kühler und eine stehende Pumpe sind zwei Nachrichten und
    /// verdienen zwei Gedächtnisse.</para>
    /// </remarks>
    public string? Meldung(int tentId, string bereich)
    {
        lock (_gate) { return _meldungen.TryGetValue(Fach(tentId, bereich), out var v) ? v : null; }
    }

    public void SetMeldung(int tentId, string bereich, string? schluessel)
    {
        lock (_gate)
        {
            var fach = Fach(tentId, bereich);
            if (schluessel is null) _meldungen.Remove(fach);
            else _meldungen[fach] = schluessel;
        }
    }

    private static string Fach(int tentId, string bereich)
        => tentId.ToString(CultureInfo.InvariantCulture) + ":" + bereich;
}
