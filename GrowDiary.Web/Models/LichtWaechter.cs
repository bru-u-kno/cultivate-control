namespace GrowDiary.Web.Models;

/// <summary>
/// Einstellungen des Licht-Wächters (Steuerung › Licht › Wächter).
/// </summary>
/// <remarks>
/// Der Wächter prüft fünf Minuten nach jeder geplanten Ein- und Ausschaltzeit mit dem
/// Lichtsensor, ob das Licht wirklich so ist, wie der Lichtplan es verlangt. Anlass war
/// der 25.10.2026: Die Betriebsart stand auf „Aus“ statt „Zeitplan“, und das Zelt blieb
/// 36 Stunden dunkel, ohne dass es jemand bemerkte.
/// </remarks>
public sealed class LichtWaechterEinstellungen
{
    public const string Korrigieren = "korrigieren";
    public const string NurMelden = "melden";

    /// <summary>Wächter an oder aus.</summary>
    public bool Aktiv { get; set; } = true;

    /// <summary>„korrigieren“: Betriebsart und Zeiten selbst zurücksetzen und melden. „melden“: nur Push — die Voreinstellung (29.10.2026): ein Wächter, der eigenmächtig schaltet, soll erst eingeschaltet werden, wenn der Bediener ihm vertraut.</summary>
    public string Modus { get; set; } = NurMelden;

    /// <summary>Der Lichtsensor gilt ab diesem Wert (in Prozent) als hell, darunter als dunkel.</summary>
    public int HellAbProzent { get; set; } = 80;

    /// <summary>Bis wann der Wächter bewusst schweigt (UTC); null = keine Pause.</summary>
    public DateTime? PauseBisUtc { get; set; }
}
