using System.Globalization;
using GrowDiary.Web.Models;
using Microsoft.Data.Sqlite;

namespace GrowDiary.Web.Infrastructure;

public sealed class NotificationSettingsRepository : RepositoryBase
{
    public NotificationSettingsRepository(AppPaths paths) : base(paths)
    {
    }

    private const string ServiceKey = "notify:service";
    private const string QuietStartKey = "notify:quietStart";
    private const string QuietEndKey = "notify:quietEnd";
    private const string ThresholdsKey = "notify:thresholds";
    private const string CalibrationKey = "notify:calibration";
    private const string MaintenanceKey = "notify:maintenance";
    private const string SensorOfflineKey = "notify:sensorOffline";
    private const string RisksKey = "notify:risks";
    private const string SystemWatchKey = "notify:systemWatch";
    private const string StartMessagesKey = "notify:startMessages";
    private const string DailyDigestKey = "notify:dailyDigest";
    private const string DigestHourKey = "notify:digestHour";
    private const string DigestMinuteKey = "notify:digestMinute";
    private const string DigestDetailedKey = "notify:digestDetailed";

    public NotificationSettings GetNotificationSettings()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Key, Value FROM AppSettings WHERE Key LIKE 'notify:%';";
        using var reader = command.ExecuteReader();

        var settings = new NotificationSettings();
        while (reader.Read())
        {
            var key = reader["Key"]?.ToString();
            var value = reader["Value"]?.ToString();
            switch (key)
            {
                case ServiceKey: settings.NotifyService = string.IsNullOrWhiteSpace(value) ? null : value; break;
                case QuietStartKey: settings.QuietHoursStartHour = ParseHour(value); break;
                case QuietEndKey: settings.QuietHoursEndHour = ParseHour(value); break;
                case ThresholdsKey: settings.Thresholds = ParseBool(value, true); break;
                case CalibrationKey: settings.Calibration = ParseBool(value, true); break;
                case MaintenanceKey: settings.Maintenance = ParseBool(value, true); break;
                case SensorOfflineKey: settings.SensorOffline = ParseBool(value, true); break;
                case RisksKey: settings.Risks = ParseBool(value, true); break;
                case SystemWatchKey: settings.SystemWatch = ParseBool(value, true); break;
                case StartMessagesKey: settings.StartMessages = ParseBool(value, true); break;
                case DailyDigestKey: settings.DailyDigest = ParseBool(value, false); break;
                case DigestHourKey: settings.DigestHour = ParseHour(value) ?? 6; break;
                case DigestMinuteKey: settings.DigestMinute = ParseMinute(value) ?? 0; break;
                case DigestDetailedKey: settings.DigestDetailed = ParseBool(value, false); break;
            }
        }

        return settings;
    }

    public void SaveNotificationSettings(NotificationSettings settings)
    {
        using var connection = OpenConnection();
        Upsert(connection, ServiceKey, string.IsNullOrWhiteSpace(settings.NotifyService) ? null : settings.NotifyService.Trim());
        Upsert(connection, QuietStartKey, settings.QuietHoursStartHour?.ToString(CultureInfo.InvariantCulture));
        Upsert(connection, QuietEndKey, settings.QuietHoursEndHour?.ToString(CultureInfo.InvariantCulture));
        Upsert(connection, ThresholdsKey, settings.Thresholds ? "1" : "0");
        Upsert(connection, CalibrationKey, settings.Calibration ? "1" : "0");
        Upsert(connection, MaintenanceKey, settings.Maintenance ? "1" : "0");
        Upsert(connection, SensorOfflineKey, settings.SensorOffline ? "1" : "0");
        Upsert(connection, RisksKey, settings.Risks ? "1" : "0");
        Upsert(connection, SystemWatchKey, settings.SystemWatch ? "1" : "0");
        Upsert(connection, StartMessagesKey, settings.StartMessages ? "1" : "0");
        Upsert(connection, DailyDigestKey, settings.DailyDigest ? "1" : "0");
        Upsert(connection, DigestHourKey, settings.DigestHour.ToString(CultureInfo.InvariantCulture));
        Upsert(connection, DigestMinuteKey, settings.DigestMinute.ToString(CultureInfo.InvariantCulture));
        Upsert(connection, DigestDetailedKey, settings.DigestDetailed ? "1" : "0");
    }

    private static int? ParseHour(string? value)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var hour) && hour is >= 0 and <= 23 ? hour : null;

    private static int? ParseMinute(string? value)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var minute) && minute is >= 0 and <= 59 ? minute : null;

    private static bool ParseBool(string? value, bool fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value is "1" or "true" or "True";

    private static void Upsert(SqliteConnection connection, string key, string? value)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO AppSettings (Key, Value)
            VALUES ($key, $value)
            ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value;
        """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", (object?)value ?? DBNull.Value);
        command.ExecuteNonQuery();
    }
}
