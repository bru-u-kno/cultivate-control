using GrowDiary.Web.Models;

namespace GrowDiary.Web.Services;

public static class SetupTentCompatibilityPolicy
{
    public static bool IsCompatible(TentType tentType, SetupType setupType)
        => tentType switch
        {
            TentType.Production => setupType == SetupType.Production,
            TentType.Mother => setupType == SetupType.Mother,
            TentType.Quarantine => setupType == SetupType.Quarantine,
            TentType.Propagation => setupType == SetupType.Propagation,
            // Trockenzelt: kein Hydro-System, keine Setups — die Ernte hängt dort nur.
            TentType.Drying => false,
            TentType.MultiPurpose => setupType is SetupType.Production or SetupType.Mother or SetupType.Quarantine or SetupType.Propagation,
            _ => false
        };
}
