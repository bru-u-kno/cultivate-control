namespace GrowDiary.Web.Models;

/// <summary>
/// Ein überwachter Controller (Geräte › Technik › Rollen › Controller · Wächter).
/// </summary>
/// <remarks>
/// Anlass (08.11.2026): Der AC-Infinity-Controller des Trockenzelts fiel mehrfach für Stunden aus
/// (gemessen: 14 Ausfälle in 10 Tagen, einer 20 Stunden lang). Beim Ausfall blieb der Feuchtewert auf dem
/// letzten Stand stehen und sah gültig aus; die Luftfeuchte im Zelt stieg unbemerkt von 54 auf 68 %,
/// bis der Controller über die Steckdose neu gestartet wurde. Der Wächter hängt am <b>Controller</b>, nicht
/// an einer Steuerung: dieselbe Hardware kann morgen etwas anderes steuern.
/// </remarks>
public sealed class ControllerWaechterEintrag
{
    public const string Neustarten = "neustart";
    public const string NurMelden = "melden";

    /// <summary>Stabile Kennung, vom Dienst vergeben.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Anzeigename, z. B. „Trockenzelt-Controller“.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Verbindungs-Entität (<c>binary_sensor</c>, Geräteklasse „connectivity“) — das Hauptsignal.</summary>
    public string? StatusEntity { get; set; }

    /// <summary>Feuchtefühler des Controllers — zweite Sicherung: bleibt der Wert stehen, gilt er als eingefroren.</summary>
    public string? FeuchteEntity { get; set; }

    /// <summary>Schaltbare Steckdose des Controllers (<c>switch</c>) für den Neustart; leer = nur melden.</summary>
    public string? SteckdoseEntity { get; set; }

    public bool Aktiv { get; set; } = true;

    /// <summary>„neustart“ (Voreinstellung seit 08.11.2026): melden und die Steckdose aus- und einschalten, sobald eine gewählt ist. „melden“: nur Push.</summary>
    public string Modus { get; set; } = Neustarten;

    /// <summary>So lange muss der Ausfall dauern, bevor der Push kommt. Kurze Aussetzer (der Status flackert oft 10 Sekunden) bleiben still.</summary>
    public int MeldenNachMin { get; set; } = 10;

    /// <summary>So lange muss der Controller offline sein, bevor die Steckdose geschaltet wird.</summary>
    public int NeustartNachMin { get; set; } = 15;

    /// <summary>So lange bleibt die Steckdose aus.</summary>
    public int AusDauerSek { get; set; } = 15;

    /// <summary>Höchstens so viele Neustarts in 24 Stunden — gegen Dauerschleifen.</summary>
    public int MaxNeustartsProTag { get; set; } = 2;

    /// <summary>Bis wann der Wächter bewusst schweigt (UTC); null = keine Pause.</summary>
    public DateTime? PauseBisUtc { get; set; }
}

/// <summary>Alle überwachten Controller.</summary>
public sealed class ControllerWaechterEinstellungen
{
    public List<ControllerWaechterEintrag> Controller { get; set; } = new();
}
