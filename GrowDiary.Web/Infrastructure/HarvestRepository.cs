using System.Globalization;
using GrowDiary.Web.Models;
using Microsoft.Data.Sqlite;

namespace GrowDiary.Web.Infrastructure;

public sealed class HarvestRepository
{
    private readonly AppPaths _paths;

    public HarvestRepository(AppPaths paths)
    {
        _paths = paths;
    }

    public int Create(HarvestEntry entry)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO HarvestEntries
                (GrowId, HarvestedAt, WetWeightG, DryWeightG, DryDays, YieldNotes,
                 Rating, FlavorNotes, EffectNotes, NugStructure, PlantWeightsJson, DryingTentId, CreatedAtUtc, UpdatedAtUtc)
            VALUES
                ($growId, $harvestedAt, $wetWeightG, $dryWeightG, $dryDays, $yieldNotes,
                 $rating, $flavorNotes, $effectNotes, $nugStructure, $plantWeightsJson, $dryingTentId, $createdAtUtc, $updatedAtUtc);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$growId",       entry.GrowId);
        command.Parameters.AddWithValue("$harvestedAt",  entry.HarvestedAt.ToString("yyyy-MM-dd"));
        command.Parameters.AddWithValue("$wetWeightG",   (object?)entry.WetWeightG  ?? DBNull.Value);
        command.Parameters.AddWithValue("$dryWeightG",   (object?)entry.DryWeightG  ?? DBNull.Value);
        command.Parameters.AddWithValue("$dryDays",      (object?)entry.DryDays     ?? DBNull.Value);
        command.Parameters.AddWithValue("$yieldNotes",   (object?)entry.YieldNotes  ?? DBNull.Value);
        command.Parameters.AddWithValue("$rating",       (object?)entry.Rating      ?? DBNull.Value);
        command.Parameters.AddWithValue("$flavorNotes",  (object?)entry.FlavorNotes ?? DBNull.Value);
        command.Parameters.AddWithValue("$effectNotes",  (object?)entry.EffectNotes ?? DBNull.Value);
        command.Parameters.AddWithValue("$nugStructure", (object?)entry.NugStructure ?? DBNull.Value);
        command.Parameters.AddWithValue("$plantWeightsJson", (object?)entry.PlantWeightsJson ?? DBNull.Value);
        command.Parameters.AddWithValue("$dryingTentId", (object?)entry.DryingTentId ?? DBNull.Value);
        command.Parameters.AddWithValue("$createdAtUtc", DateTime.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$updatedAtUtc", DateTime.UtcNow.ToString("O"));
        return Convert.ToInt32((long)(command.ExecuteScalar() ?? 0L));
    }

    public HarvestEntry? GetForGrow(int growId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT * FROM HarvestEntries WHERE GrowId = $growId ORDER BY HarvestedAt DESC LIMIT 1;
            """;
        command.Parameters.AddWithValue("$growId", growId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return Map(reader);
    }

    /// <summary>Die Ernten, die in diesem Trockenzelt hängen und noch nicht gewogen sind (neueste Ernte zuerst).</summary>
    public List<HarvestEntry> GetDryingInTent(int tentId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT * FROM HarvestEntries
            WHERE DryingTentId = $tentId AND DryWeightG IS NULL
            ORDER BY HarvestedAt DESC;
            """;
        command.Parameters.AddWithValue("$tentId", tentId);
        using var reader = command.ExecuteReader();
        var list = new List<HarvestEntry>();
        while (reader.Read()) list.Add(Map(reader));
        return list;
    }

    public void Update(HarvestEntry entry)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE HarvestEntries SET
                HarvestedAt  = $harvestedAt,
                WetWeightG   = $wetWeightG,
                DryWeightG   = $dryWeightG,
                DryDays      = $dryDays,
                YieldNotes   = $yieldNotes,
                Rating       = $rating,
                FlavorNotes  = $flavorNotes,
                EffectNotes  = $effectNotes,
                NugStructure = $nugStructure,
                PlantWeightsJson = $plantWeightsJson,
                DryingTentId = $dryingTentId,
                UpdatedAtUtc = $updatedAtUtc
            WHERE Id = $id;
            """;
        command.Parameters.AddWithValue("$id",           entry.Id);
        command.Parameters.AddWithValue("$harvestedAt",  entry.HarvestedAt.ToString("yyyy-MM-dd"));
        command.Parameters.AddWithValue("$wetWeightG",   (object?)entry.WetWeightG  ?? DBNull.Value);
        command.Parameters.AddWithValue("$dryWeightG",   (object?)entry.DryWeightG  ?? DBNull.Value);
        command.Parameters.AddWithValue("$dryDays",      (object?)entry.DryDays     ?? DBNull.Value);
        command.Parameters.AddWithValue("$yieldNotes",   (object?)entry.YieldNotes  ?? DBNull.Value);
        command.Parameters.AddWithValue("$rating",       (object?)entry.Rating      ?? DBNull.Value);
        command.Parameters.AddWithValue("$flavorNotes",  (object?)entry.FlavorNotes ?? DBNull.Value);
        command.Parameters.AddWithValue("$effectNotes",  (object?)entry.EffectNotes ?? DBNull.Value);
        command.Parameters.AddWithValue("$nugStructure", (object?)entry.NugStructure ?? DBNull.Value);
        command.Parameters.AddWithValue("$plantWeightsJson", (object?)entry.PlantWeightsJson ?? DBNull.Value);
        command.Parameters.AddWithValue("$dryingTentId", (object?)entry.DryingTentId ?? DBNull.Value);
        command.Parameters.AddWithValue("$updatedAtUtc", DateTime.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    private static string? ColumnOrNull(SqliteDataReader reader, string name)
    {
        for (var index = 0; index < reader.FieldCount; index += 1)
        {
            if (!string.Equals(reader.GetName(index), name, StringComparison.OrdinalIgnoreCase)) continue;
            return reader.IsDBNull(index) ? null : reader.GetString(index);
        }

        return null;
    }

    private static HarvestEntry Map(SqliteDataReader reader)
        => new()
        {
            Id           = Convert.ToInt32((long)reader["Id"]),
            GrowId       = Convert.ToInt32((long)reader["GrowId"]),
            HarvestedAt  = ParseDateOrDefault(reader["HarvestedAt"]),
            WetWeightG   = reader["WetWeightG"]  is DBNull ? null : Convert.ToDouble(reader["WetWeightG"]),
            DryWeightG   = reader["DryWeightG"]  is DBNull ? null : Convert.ToDouble(reader["DryWeightG"]),
            DryDays      = reader["DryDays"]     is DBNull ? null : Convert.ToInt32((long)reader["DryDays"]),
            YieldNotes   = reader["YieldNotes"]  is DBNull ? null : reader["YieldNotes"].ToString(),
            Rating       = reader["Rating"]      is DBNull ? null : Convert.ToDouble(reader["Rating"]),
            FlavorNotes  = reader["FlavorNotes"] is DBNull ? null : reader["FlavorNotes"].ToString(),
            EffectNotes  = reader["EffectNotes"] is DBNull ? null : reader["EffectNotes"].ToString(),
            NugStructure = reader["NugStructure"] is DBNull ? null : reader["NugStructure"].ToString(),
            // Defensiv: die Spalte kam spaeter dazu, und dieses Repository erbt
            // nicht von RepositoryBase, hat also dessen HasColumn nicht.
            PlantWeightsJson = ColumnOrNull(reader, "PlantWeightsJson"),
            DryingTentId = int.TryParse(ColumnOrNull(reader, "DryingTentId"), CultureInfo.InvariantCulture, out var trockenzelt) ? trockenzelt : null,
            CreatedAtUtc = ParseUtcOrDefault(reader["CreatedAtUtc"]),
            UpdatedAtUtc = ParseUtcOrDefault(reader["UpdatedAtUtc"])
        };

    private static DateTime ParseUtcOrDefault(object raw)
    {
        var text = raw is DBNull ? null : raw?.ToString();
        if (!string.IsNullOrWhiteSpace(text) &&
            DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal, out var parsed))
        {
            return parsed.ToUniversalTime();
        }

        return DateTime.UtcNow;
    }

    private static DateTime ParseDateOrDefault(object raw)
    {
        var text = raw is DBNull ? null : raw?.ToString();
        if (!string.IsNullOrWhiteSpace(text) &&
            DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var parsed))
        {
            return parsed.Date;
        }

        return DateTime.Today;
    }

    private SqliteConnection OpenConnection()
    {
        var builder = new SqliteConnectionStringBuilder { DataSource = _paths.DatabasePath };
        var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON;";
        pragma.ExecuteNonQuery();
        return connection;
    }
}
