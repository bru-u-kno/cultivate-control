using System.Globalization;
using System.Text.Json;
using GrowDiary.Web.Services.Knowledge.Schema;
using Microsoft.Data.Sqlite;

namespace GrowDiary.Web.Infrastructure;

/// <summary>
/// Der eigene Trocknungsplan je Trockenzelt (Etappen mit Dauer, Temperatur und Luftfeuchte) — unabhängig vom Düngeprogramm.
/// </summary>
/// <remarks>
/// Eigene Tabelle (<c>ForkTrocknungPlan</c>) mit dem Zelt als Schlüssel. Die Etappen liegen als Liste in einer Spalte: Sie werden nur
/// als Ganzes gelesen und gespeichert. Bis cc.298 hingen sie am Nährstoffprogramm; <c>TrocknungPlanQuelle</c> übernimmt einmalig
/// eigene Etappen eines Programms, danach gilt dieser Plan.
/// </remarks>
public sealed class TrocknungPlanRepository : RepositoryBase
{
    public TrocknungPlanRepository(AppPaths paths) : base(paths)
    {
    }

    private SqliteConnection Open()
    {
        var connection = OpenConnection();
        EigenesSchema.Sicherstellen(nameof(TrocknungPlanRepository), Paths.DatabasePath, () => SchemaAnlegen(connection));
        return connection;
    }

    private static void SchemaAnlegen(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS ForkTrocknungPlan (
                TentId INTEGER NOT NULL PRIMARY KEY,
                EtappenJson TEXT NOT NULL,
                UpdatedAtUtc TEXT NOT NULL
            );
            """;
        command.ExecuteNonQuery();
    }

    /// <summary>Die gespeicherten Etappen dieses Zelts, oder <c>null</c>, wenn es noch keinen eigenen Plan hat.</summary>
    public List<TrockenEtappe>? Get(int tentId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT EtappenJson FROM ForkTrocknungPlan WHERE TentId = $tentId;";
        command.Parameters.AddWithValue("$tentId", tentId);
        if (command.ExecuteScalar() is not string json) return null;
        try
        {
            return JsonSerializer.Deserialize<List<TrockenEtappe>>(json) is { Count: > 0 } liste ? liste : null;
        }
        catch (JsonException)
        {
            return null; // unlesbar: so tun, als gäbe es keinen eigenen Plan — der Standard gilt, statt dass die Seite bricht
        }
    }

    public void Set(int tentId, IReadOnlyList<TrockenEtappe> etappen)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ForkTrocknungPlan (TentId, EtappenJson, UpdatedAtUtc) VALUES ($tentId, $json, $jetzt)
            ON CONFLICT(TentId) DO UPDATE SET EtappenJson = excluded.EtappenJson, UpdatedAtUtc = excluded.UpdatedAtUtc;
            """;
        command.Parameters.AddWithValue("$tentId", tentId);
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(etappen));
        command.Parameters.AddWithValue("$jetzt", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }
}
