namespace GrowDiary.Web.Api.Contracts;

public sealed record NotificationSettingsDto(
    string? NotifyService,
    int? QuietHoursStartHour,
    int? QuietHoursEndHour,
    bool Thresholds,
    bool Calibration,
    bool Maintenance,
    bool SensorOffline,
    bool Risks,
    bool SystemWatch,
    bool StartMessages,
    bool DailyDigest,
    int DigestHour,
    int DigestMinute,
    bool DigestDetailed);

public sealed record NotificationTestRequest(string? NotifyService);
