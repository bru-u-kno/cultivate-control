namespace GrowDiary.Web.Api.Contracts;

public sealed record TentDto(
    int Id,
    string Name,
    string Kind,
    string TentType,
    string Status,
    string? Notes,
    int DisplayOrder,
    string AccentColor,
    int? WidthCm,
    int? DepthCm,
    int? TentHeightCm,
    string? LightType,
    int? LightWatt,
    string? LightController,
    string? LightControllerEntityId,
    int? ExhaustFanCount,
    int? ExhaustM3h,
    int? CirculationFanCount,
    string? HvacController,
    string? HvacControllerEntityId,
    bool Co2Available,
    bool HasCo2Enrichment,
    string? RaumId,
    string? CameraEntityId,
    int ActiveGrowCount,
    int ArchivedGrowCount,
    int ActiveSetupCount,
    int ArchivedSetupCount,
    IReadOnlyList<TentSensorDto> Sensors,
    IReadOnlyList<string> Cameras,
    double LeafTempOffsetC,
    string? LeafOffsetSyncService,
    int LeafOffsetSyncPort,
    // Aus den Maßen gerechnet (ZeltMasse) — nicht eingetragen. null, solange Maße oder Abluft fehlen.
    double? VolumeM3 = null,
    double? FloorAreaM2 = null,
    double? AirChangesPerHour = null
);

public sealed record TentSensorDto(
    int Id,
    int TentId,
    string MetricType,
    string HaEntityId,
    string? DisplayLabel,
    bool IsActive
);
