using System.Text.Json.Nodes;
using GrowDiary.Web.Models;
using GrowDiary.Web.Services;

namespace GrowDiary.Web.Tests.Models;

/// <summary>
/// Der Katalog, die Rollen und die Vorlagen der CO₂-Steuerung eines weiteren Zeltes: abgeleitet aus dem Bestand, ohne dass sich
/// am Bestand-Zelt etwas ändert.
/// </summary>
[Collection(GrowDiary.Web.Tests.Services.Co2ZelteRegisterCollection.Name)]
public sealed class Co2ZeltKatalogTests : IDisposable
{
    private readonly Co2Zelte.Stand _vorher = Co2Zelte.Aktuell;

    public Co2ZeltKatalogTests() => Co2Zelte.Festlegen(1, [1, 2]);

    public void Dispose() => Co2Zelte.Festlegen(_vorher.BestandZeltId, _vorher.ZeltIds);

    [Fact]
    public void ZeltKatalog_HatSovieleBauteileWieDerBestand_AllesMitZeltKennung()
    {
        var bestand = SteuerungBauteile.FuerModul("co2");
        var zelt = SteuerungBauteile.FuerModul("co2-z2");

        Assert.NotEmpty(bestand); // Selbsttest: der Test sieht den Katalog überhaupt
        Assert.Equal(bestand.Count, zelt.Count);
        Assert.All(zelt, b =>
        {
            Assert.Equal("co2-z2", b.Modul);
            Assert.StartsWith("co2_z2_", b.Objektkennung);
            Assert.StartsWith("CO2 Z2 ", b.Name);
        });
        // Keine Kennung teilen sich Bestand und Zelt — sonst schrieben zwei Regelungen in denselben Helfer.
        Assert.Empty(zelt.Select(b => b.EntityId).Intersect(bestand.Select(b => b.EntityId), StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void BestandKatalog_BleibtUnveraendert_AlleKennungenMitCo2Praefix()
    {
        var bestand = SteuerungBauteile.FuerModul("co2");
        Assert.All(bestand, b => Assert.DoesNotMatch(@"co2_z\d+_", b.Objektkennung));
        Assert.Contains(bestand, b => b.EntityId == "input_number.co2_zielwert");
    }

    [Fact]
    public void ZeltKatalog_NameUndKennungPassenNachHomeAssistantRegelZusammen()
    {
        static string Slug(string name) => System.Text.RegularExpressions.Regex.Replace(
            name.ToLowerInvariant().Replace("ä", "a"), "[^a-z0-9]+", "_").Trim('_');
        foreach (var b in SteuerungBauteile.FuerModul("co2-z2"))
        {
            // Die Bestandsliste hat für ein paar Bauteile bewusst abweichende Namen (siehe Katalog-Doku); es zählt, dass der
            // abgeleitete Name dieselbe Abweichung trägt wie der Bestand — also nur das Zeltstück neu hinzukommt.
            var bestand = SteuerungBauteile.FuerModul("co2").Single(x => x.EntityId == Co2Modul.Id(b.EntityId, null).Replace("co2_z2_", "co2_"));
            Assert.Equal(Slug(bestand.Name) == bestand.Objektkennung, Slug(b.Name) == b.Objektkennung);
        }
    }

    [Fact]
    public void ZeltKatalog_RechenvorschriftenVerweisenNurAufDieEigenenHelfer()
    {
        var rechen = SteuerungBauteile.FuerModul("co2-z2").Where(b => b.Vorlage is not null).ToList();
        Assert.NotEmpty(rechen);
        foreach (var b in rechen)
        {
            Assert.DoesNotMatch(@"\b(?:input_number|input_boolean|input_datetime|counter|sensor|binary_sensor|automation)\.co2_(?!z2_)", b.Vorlage!);
        }
    }

    [Fact]
    public void FindenNachId_FindetBestandUndZeltBauteil()
    {
        Assert.Equal("co2", SteuerungBauteile.FindenNachId("input_number.co2_zielwert")?.Modul);
        Assert.Equal("co2-z2", SteuerungBauteile.FindenNachId("input_number.co2_z2_zielwert")?.Modul);
        Assert.Null(SteuerungBauteile.FindenNachId("input_number.co2_z3_zielwert")); // Zelt 3 hat keine CO₂-Steuerung
    }

    [Fact]
    public void Rollen_GeltenFuerJedesCo2Modul()
    {
        var bestand = SteuerungGeraeteRollen.FuerModul("co2");
        Assert.NotEmpty(bestand);
        Assert.Equal(bestand.Select(r => r.Schluessel), SteuerungGeraeteRollen.FuerModul("co2-z2").Select(r => r.Schluessel));
        Assert.NotNull(SteuerungGeraeteRollen.Finden("co2-z2", "co2_sensor"));
        Assert.Empty(SteuerungGeraeteRollen.FuerModul("zuluft-z2")); // kein CO₂-Modul, kein Zufallstreffer
    }

    [Fact]
    public void Vorlage_FuerEinZelt_StelltKennungenUndAliasUm_AusgangsvorlageBleibtHeil()
    {
        var pfad = Path.Combine(AppContext.BaseDirectory, "Vorlagen", "co2", "dosierung.json");
        var original = JsonNode.Parse(File.ReadAllText(pfad))!.AsObject();
        var vorher = original.ToJsonString();

        var zelt = Co2Modul.Vorlage(original, 2);

        Assert.Equal(vorher, original.ToJsonString());               // Eingabe unverändert
        Assert.Equal("CO2 Z2 Dosierung", zelt["alias"]!.GetValue<string>());
        var text = zelt.ToJsonString();
        Assert.Contains("input_number.co2_z2_flasche_rest", text);
        Assert.Contains("binary_sensor.co2_z2_bedarf", text);
        Assert.DoesNotMatch(@"\b(?:input_number|input_boolean|input_datetime|counter|sensor|binary_sensor|automation)\.co2_(?!z2_)", text);
        Assert.Contains("[[co2_sensor]]", text);                     // Rolle des Nutzers bleibt Platzhalter
        Assert.Same(original, Co2Modul.Vorlage(original, null));     // Bestand: dieselbe Vorlage
    }

    [Fact]
    public void AlleVorlagen_StelltManFuerEinZeltUm_BleibtDasEinGueltigesObjektMitAlias()
    {
        var ordner = Path.Combine(AppContext.BaseDirectory, "Vorlagen", "co2");
        var dateien = Directory.GetFiles(ordner, "*.json");
        Assert.True(dateien.Length >= 4);
        foreach (var datei in dateien)
        {
            var zelt = Co2Modul.Vorlage(JsonNode.Parse(File.ReadAllText(datei))!.AsObject(), 5);
            Assert.StartsWith("CO2 Z5 ", zelt["alias"]!.GetValue<string>());
        }
    }
}
