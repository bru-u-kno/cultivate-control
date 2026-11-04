using System.Text.Json;
using System.Text.Json.Nodes;
using GrowDiary.Web.Models;

namespace GrowDiary.Web.Services;

/// <summary>
/// Etappe 2, Release 5c: Die Bausteine einer weiteren Instanz (Automationen, Rechenwerte, Helfer) aus Home Assistant entfernen —
/// auf ausdrücklichen Wunsch beim Löschen der Instanz.
/// </summary>
/// <remarks>
/// <para><b>Nur Instanzen, nie die erste Steuerung.</b> <see cref="Planen"/> kennt nur Module der Form <c>{art}-i{n}</c> und nur
/// deren Kennungen (alle mit der Endung <c>_i{n}</c>). Für <c>chiller</c> selbst liefert es nichts — ein Fehler im Aufrufer kann
/// den Bestand des Nutzers nicht löschen. Der Plan ist rein und ohne Home Assistant prüfbar.</para>
/// <para><b>Reihenfolge:</b> erst die Automationen (sie lesen die Helfer), dann die Rechenwerte (Template-Einträge), zuletzt die
/// Helfer. Automationen werden vorher als Datei gesichert; eine Automation ohne Herkunftsmarke des Fork ist von Hand gebaut und
/// bleibt stehen.</para>
/// <para>Was nicht (mehr) in Home Assistant steht, ist kein Fehler: das Entfernen kann wiederholt werden.</para>
/// </remarks>
public sealed class SteuerungInstanzAufraeumenService
{
    private const string AutomationPfad = "api/config/automation/config";

    /// <summary>Ein Baustein, der entfernt werden soll.</summary>
    /// <param name="Art"><c>automation</c>, <c>rechenwert</c> oder <c>helfer</c>.</param>
    /// <param name="Kennung">Automation: Konfigurations-Kennung; Rechenwert und Helfer: Entitäts-Kennung.</param>
    /// <param name="Titel">Rechenwert: der Titel des Template-Eintrags; sonst der Name.</param>
    public sealed record Baustein(string Art, string Kennung, string Titel);

    /// <summary>Was mit einem Baustein geschehen ist: <c>entfernt</c>, <c>nicht_da</c>, <c>fremd</c> (von Hand gebaut, unangetastet) oder <c>fehler</c>.</summary>
    public sealed record Teil(string Art, string Kennung, string Stand, string? Hinweis);

    public sealed record Bilanz(bool Erreichbar, IReadOnlyList<Teil> Einzeln)
    {
        public int Entfernt => Einzeln.Count(t => t.Stand == "entfernt");
        public int Fehler => Einzeln.Count(t => t.Stand == "fehler");

        /// <summary>Alles, was in Home Assistant stand und dem Fork gehörte, ist weg.</summary>
        public bool Vollstaendig => Erreichbar && Fehler == 0;
    }

    /// <summary>Löscht Helfer einer Domäne über den WebSocket; Schlüssel der Antwort ist die Entitäts-Kennung.</summary>
    public delegate Task<IReadOnlyDictionary<string, (bool Ok, string? Fehler)>> HelferLoescher(
        HomeAssistantSettings settings, IReadOnlyList<Baustein> helfer, CancellationToken ct);

    private readonly HomeAssistantService _ha;
    private readonly SteuerungAutomationService _automationen;
    private readonly ILogger<SteuerungInstanzAufraeumenService> _log;
    private readonly HelferLoescher _helferLoeschen;
    private readonly string _vorlagenWurzel;

    public SteuerungInstanzAufraeumenService(
        HomeAssistantService ha, SteuerungAutomationService automationen, ILogger<SteuerungInstanzAufraeumenService> log,
        HelferLoescher? helferLoeschen = null, string? vorlagenWurzel = null)
    {
        _ha = ha;
        _automationen = automationen;
        _log = log;
        _helferLoeschen = helferLoeschen ?? HelferUeberSocketAsync;
        _vorlagenWurzel = vorlagenWurzel ?? SteuerungAutomationService.VorlagenWurzel;
    }

    // ------------------------------------------------------------------ Plan (rein)

    /// <summary>
    /// Was zu einer Instanz gehört — in der Reihenfolge, in der es entfernt wird. Für ein Modul, das keine weitere Instanz ist,
    /// ist die Liste leer.
    /// </summary>
    public static IReadOnlyList<Baustein> Planen(string modul, string? vorlagenWurzel = null)
    {
        if (InstanzModul.Nummer(modul) is not { } nummer) return [];
        var katalog = SteuerungBauteile.FuerModul(modul);
        var endung = $"_i{nummer}";
        var plan = new List<Baustein>();

        // Automationen: die Vorlagen der Art (so legt der Fork sie an) und die des Katalogs.
        var kennungen = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var b in katalog.Where(b => b.Art == BauteilArt.Automation && b.KonfigKennung is not null)) kennungen.Add(b.KonfigKennung!);
        var ordner = Path.Combine(vorlagenWurzel ?? SteuerungAutomationService.VorlagenWurzel, InstanzModul.BasisModul(modul));
        if (Directory.Exists(ordner))
        {
            foreach (var datei in Directory.EnumerateFiles(ordner, "*.json"))
                kennungen.Add(SteuerungBauteile.AutomationsKennung(modul, Path.GetFileNameWithoutExtension(datei)));
        }
        plan.AddRange(kennungen.Select(k => new Baustein("automation", k, k)));

        plan.AddRange(katalog.Where(b => b.Art is BauteilArt.RechenSensor or BauteilArt.RechenSchalter && b.EntityId.EndsWith(endung, StringComparison.Ordinal))
            .Select(b => new Baustein("rechenwert", b.EntityId, b.Name)));
        // Gleitende Mittelwerte sind Filter-Helfer (nicht Template): eigener Eintrag in Home Assistant, eigene Domäne beim Entfernen.
        plan.AddRange(katalog.Where(b => b.Art == BauteilArt.Mittelwert && b.EntityId.EndsWith(endung, StringComparison.Ordinal))
            .Select(b => new Baustein("mittelwert", b.EntityId, b.Name)));
        plan.AddRange(katalog.Where(b => SteuerungHelferService.Kann(b.Art) && b.EntityId.EndsWith(endung, StringComparison.Ordinal))
            .Select(b => new Baustein("helfer", b.EntityId, b.Name)));
        return plan;
    }

    // ------------------------------------------------------------------ Ausführen

    public async Task<Bilanz> EntfernenAsync(string modul, HomeAssistantSettings settings, CancellationToken ct = default)
    {
        var plan = Planen(modul, _vorlagenWurzel);
        if (plan.Count == 0 || !settings.IsConfigured) return new Bilanz(false, []);

        // Ohne Antwort von Home Assistant ist nicht zu erkennen, was es gibt — dann wird nichts angefasst.
        var vorhanden = (await _ha.GetEntitiesAsync(settings, ct)).Select(e => e.EntityId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (vorhanden.Count == 0) return new Bilanz(false, []);

        var einzeln = new List<Teil>();
        using var client = _ha.CreateClient(settings, _ha.Dienstfrist);

        foreach (var b in plan.Where(p => p.Art == "automation")) einzeln.Add(await AutomationEntfernenAsync(client, b, ct));

        var rechenwerte = plan.Where(p => p.Art == "rechenwert").ToList();
        if (rechenwerte.Count > 0) einzeln.AddRange(await RechenwerteEntfernenAsync(client, rechenwerte, "template", "rechenwert", ct));
        var mittelwerte = plan.Where(p => p.Art == "mittelwert").ToList();
        if (mittelwerte.Count > 0) einzeln.AddRange(await RechenwerteEntfernenAsync(client, mittelwerte, "filter", "mittelwert", ct));

        var helfer = plan.Where(p => p.Art == "helfer").ToList();
        var da = helfer.Where(h => vorhanden.Contains(h.Kennung)).ToList();
        einzeln.AddRange(helfer.Where(h => !vorhanden.Contains(h.Kennung)).Select(h => new Teil("helfer", h.Kennung, "nicht_da", null)));
        if (da.Count > 0)
        {
            var ergebnisse = await _helferLoeschen(settings, da, ct);
            foreach (var h in da)
            {
                var (ok, fehler) = ergebnisse.TryGetValue(h.Kennung, out var e) ? e : (false, "Keine Antwort von Home Assistant.");
                einzeln.Add(new Teil("helfer", h.Kennung, ok ? "entfernt" : "fehler", ok ? null : fehler));
            }
        }

        _log.LogInformation("Instanz {Modul} aus Home Assistant entfernt: {Entfernt} Bausteine, {Fehler} Fehler.", modul, einzeln.Count(t => t.Stand == "entfernt"), einzeln.Count(t => t.Stand == "fehler"));
        return new Bilanz(true, einzeln);
    }

    private async Task<Teil> AutomationEntfernenAsync(HttpClient client, Baustein b, CancellationToken ct)
    {
        try
        {
            using var gelesen = await client.GetAsync($"{AutomationPfad}/{Uri.EscapeDataString(b.Kennung)}", ct);
            if (gelesen.StatusCode == System.Net.HttpStatusCode.NotFound) return new Teil("automation", b.Kennung, "nicht_da", null);
            if (!gelesen.IsSuccessStatusCode) return new Teil("automation", b.Kennung, "fehler", $"Lesen: {(int)gelesen.StatusCode}.");

            var config = JsonNode.Parse(await gelesen.Content.ReadAsStringAsync(ct)) as JsonObject;
            // Ohne die Marke des Fork hat sie jemand von Hand gebaut — sie bleibt.
            if (SteuerungAutomationService.Fassung(config?["description"]?.GetValue<string>()) is null)
                return new Teil("automation", b.Kennung, "fremd", "Von Hand gebaut — nicht entfernt.");

            if (!await _automationen.SichernAsync(client, b.Kennung, ct))
                return new Teil("automation", b.Kennung, "fehler", "Die Sicherung vorher ist fehlgeschlagen — nichts entfernt.");

            using var geloescht = await client.DeleteAsync($"{AutomationPfad}/{Uri.EscapeDataString(b.Kennung)}", ct);
            return geloescht.IsSuccessStatusCode
                ? new Teil("automation", b.Kennung, "entfernt", null)
                : new Teil("automation", b.Kennung, "fehler", $"Entfernen: {(int)geloescht.StatusCode}.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return new Teil("automation", b.Kennung, "fehler", ex.Message);
        }
    }

    private async Task<IEnumerable<Teil>> RechenwerteEntfernenAsync(HttpClient client, IReadOnlyList<Baustein> rechenwerte, string domaene, string art, CancellationToken ct)
    {
        var ergebnis = new List<Teil>();
        JsonElement eintraege;
        try
        {
            using var antwort = await client.GetAsync($"api/config/config_entries/entry?domain={domaene}", ct);
            if (!antwort.IsSuccessStatusCode)
                return rechenwerte.Select(r => new Teil(art, r.Kennung, "fehler", $"Eintragsliste nicht lesbar ({(int)antwort.StatusCode})."));
            eintraege = await antwort.Content.ReadFromJsonAsync<JsonElement>(ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return rechenwerte.Select(r => new Teil(art, r.Kennung, "fehler", ex.Message));
        }

        foreach (var r in rechenwerte)
        {
            var eintrag = eintraege.ValueKind == JsonValueKind.Array
                ? eintraege.EnumerateArray().FirstOrDefault(e => e.TryGetProperty("title", out var t) && t.GetString() == r.Titel)
                : default;
            var kennung = eintrag.ValueKind == JsonValueKind.Object && eintrag.TryGetProperty("entry_id", out var id) ? id.GetString() : null;
            if (kennung is null) { ergebnis.Add(new Teil(art, r.Kennung, "nicht_da", null)); continue; }

            try
            {
                using var geloescht = await client.DeleteAsync($"api/config/config_entries/entry/{Uri.EscapeDataString(kennung)}", ct);
                ergebnis.Add(geloescht.IsSuccessStatusCode
                    ? new Teil(art, r.Kennung, "entfernt", null)
                    : new Teil(art, r.Kennung, "fehler", $"Entfernen: {(int)geloescht.StatusCode}."));
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                ergebnis.Add(new Teil(art, r.Kennung, "fehler", ex.Message));
            }
        }

        return ergebnis;
    }

    /// <summary>Die Helfer über den WebSocket löschen: <c>input_number/delete</c> und Geschwister, Schlüssel <c>{domäne}_id</c>.</summary>
    private static async Task<IReadOnlyDictionary<string, (bool Ok, string? Fehler)>> HelferUeberSocketAsync(
        HomeAssistantSettings settings, IReadOnlyList<Baustein> helfer, CancellationToken ct)
    {
        var ergebnis = new Dictionary<string, (bool, string?)>(StringComparer.OrdinalIgnoreCase);
        await using var socket = await HomeAssistantSocket.OeffnenAsync(settings, ct);
        if (socket is null)
        {
            foreach (var h in helfer) ergebnis[h.Kennung] = (false, "Home Assistant nimmt die WebSocket-Anmeldung nicht an.");
            return ergebnis;
        }

        foreach (var h in helfer)
        {
            var teile = h.Kennung.Split('.', 2);
            var antwort = await socket.BefehlAsync($"{teile[0]}/delete",
                new Dictionary<string, object?>(StringComparer.Ordinal) { [$"{teile[0]}_id"] = teile[1] }, ct);
            ergebnis[h.Kennung] = (antwort.Erfolg, antwort.Fehler);
        }

        return ergebnis;
    }
}
