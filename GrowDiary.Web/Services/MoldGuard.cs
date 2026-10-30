using GrowDiary.Web.Models;

namespace GrowDiary.Web.Services;

/// <summary>
/// Wie feucht es je Phase höchstens sein darf, bevor Schimmel wahrscheinlicher
/// wird als Nutzen.
/// </summary>
/// <remarks>
/// <para>Warum das eine eigene Grenze braucht: die Feuchte-Empfehlung wird aus
/// dem VPD-Ziel zurückgerechnet, und diese Rechnung kennt nur Physik. Bei 32 °C
/// in der Blüte käme „Ziel 64–68 % RLF" heraus — physikalisch korrekt, praktisch
/// eine Einladung für Grauschimmel mitten in den Blüten. Was ausgeflockt ist,
/// merkt man erst beim Trimmen.</para>
///
/// <para>Die Werte folgen der gängigen Praxis: junge Pflanzen ohne Blüten
/// vertragen und wollen viel Feuchte; sobald dichte Blüten da sind, hält Wasser
/// sich in ihnen — ab ~60 % steigt das Botrytis-Risiko deutlich, in der
/// Spätblüte fährt man eher 45–55 %. Beim Trocknen gilt die 60/60-Faustregel.</para>
///
/// <para>Wichtig fürs Verständnis: das ist ein Deckel für unsere EMPFEHLUNG.
/// Ein Grenzwert, den der Nutzer selbst einträgt, gewinnt weiterhin — wie
/// überall.</para>
/// </remarks>
public static class MoldGuard
{
    public static double MaxHumidityPercent(GrowStage stage) => stage switch
    {
        GrowStage.Seedling or GrowStage.Clone => 80,
        GrowStage.Veg => 70,
        GrowStage.Transition => 65,
        GrowStage.Flower => 60,
        GrowStage.Finish => 55,
        GrowStage.Dry => 60,
        GrowStage.Cure => 62,
        _ => 70,
    };

    // Das Klima beim Trocknen stand hier als feste Zahlen (18–20 °C, 55–60 %, die 60/60-Faustregel).
    // Seit A-029 führt es das Programm als Etappen — Standard und Rechnung in TrocknungsPlan.
    // Nach der Ernte hängt alles kopfüber im Zelt, 7 bis 14 Tage, und es ist das höchste Schimmelrisiko
    // des ganzen Zyklus: zu warm und trocken trocknet es in drei Tagen und schmeckt nach Heu, zu feucht
    // schimmelt es von innen.

    /// <summary>
    /// So lange nach der Ernte gilt das Zelt als Trockenraum, wenn kein
    /// Trockengewicht eingetragen ist. Danach ist realistisch längst alles im
    /// Glas — der Modus soll nicht ewig kleben.
    /// </summary>
    public const int DryingWindowDays = 21;
}
