using System.Globalization;
using Microsoft.Data.Sqlite;

namespace GrowDiary.Web.Infrastructure;

/// <summary>
/// Fork AI (A-029, Etappe 3, Teil A): welcher Port des Controllers ist am Trockenzelt der Entfeuchter, welcher die Kühlung.
/// </summary>
/// <remarks>
/// Eigene Tabelle (<c>ForkTrocknungPorts</c>) mit dem Zelt als Schlüssel — die bestehende Geräte-Zuordnung kennt kein Zelt,
/// und die Grow-Helfer sollen dafür nicht umgebaut werden (der Plan des Grow-Zelts bleibt davon unberührt).
/// Gespeichert wird die <b>Geräte-Id</b> des Ports in Home Assistant, nicht eine Entity-Id: Aus dem Port ergeben sich Modus
/// und Auslöser über die stabile Kennung (siehe <c>TrocknungZiel</c>).
/// </remarks>
public sealed class TrocknungPortRepository : RepositoryBase
{
    public TrocknungPortRepository(AppPaths paths) : base(paths)
    {
    }

    private SqliteConnection Open()
    {
        var connection = OpenConnection();
        EigenesSchema.Sicherstellen(nameof(TrocknungPortRepository), Paths.DatabasePath, () => SchemaAnlegen(connection));
        return connection;
    }

    private static void SchemaAnlegen(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS ForkTrocknungPorts (
                TentId INTEGER NOT NULL,
                Rolle TEXT NOT NULL,
                DeviceId TEXT NOT NULL,
                UpdatedAtUtc TEXT NOT NULL,
                PRIMARY KEY (TentId, Rolle)
            );
            """;
        command.ExecuteNonQuery();
    }

    /// <summary>Rolle → Geräte-Id, für dieses Zelt.</summary>
    public Dictionary<string, string> Get(int tentId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Rolle, DeviceId FROM ForkTrocknungPorts WHERE TentId = $tentId;";
        command.Parameters.AddWithValue("$tentId", tentId);
        using var reader = command.ExecuteReader();
        var zuordnung = new Dictionary<string, string>(StringComparer.Ordinal);
        while (reader.Read()) zuordnung[reader.GetString(0)] = reader.GetString(1);
        return zuordnung;
    }

    /// <summary>Setzt die Zuordnung einer Rolle; <c>null</c> oder leer löst sie.</summary>
    public void Set(int tentId, string rolle, string? deviceId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            command.CommandText = "DELETE FROM ForkTrocknungPorts WHERE TentId = $tentId AND Rolle = $rolle;";
        }
        else
        {
            command.CommandText = """
                INSERT INTO ForkTrocknungPorts (TentId, Rolle, DeviceId, UpdatedAtUtc) VALUES ($tentId, $rolle, $deviceId, $jetzt)
                ON CONFLICT(TentId, Rolle) DO UPDATE SET DeviceId = excluded.DeviceId, UpdatedAtUtc = excluded.UpdatedAtUtc;
                """;
            command.Parameters.AddWithValue("$deviceId", deviceId.Trim());
            command.Parameters.AddWithValue("$jetzt", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        }

        command.Parameters.AddWithValue("$tentId", tentId);
        command.Parameters.AddWithValue("$rolle", rolle);
        command.ExecuteNonQuery();
    }
}
