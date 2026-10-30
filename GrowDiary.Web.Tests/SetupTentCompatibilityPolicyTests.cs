using GrowDiary.Web.Models;
using GrowDiary.Web.Services;

namespace GrowDiary.Web.Tests;

public sealed class SetupTentCompatibilityPolicyTests
{
    [Theory]
    [InlineData(TentType.Production, SetupType.Production, true)]
    [InlineData(TentType.Production, SetupType.Mother, false)]
    [InlineData(TentType.Production, SetupType.Quarantine, false)]
    [InlineData(TentType.Mother, SetupType.Production, false)]
    [InlineData(TentType.Mother, SetupType.Mother, true)]
    [InlineData(TentType.Mother, SetupType.Quarantine, false)]
    [InlineData(TentType.Quarantine, SetupType.Production, false)]
    [InlineData(TentType.Quarantine, SetupType.Mother, false)]
    [InlineData(TentType.Quarantine, SetupType.Quarantine, true)]
    [InlineData(TentType.Propagation, SetupType.Propagation, true)]
    public void IsCompatible_EnforcesDedicatedTentRules(TentType tentType, SetupType setupType, bool expected)
    {
        Assert.Equal(expected, SetupTentCompatibilityPolicy.IsCompatible(tentType, setupType));
    }

    [Theory]
    [InlineData(SetupType.Production)]
    [InlineData(SetupType.Mother)]
    [InlineData(SetupType.Quarantine)]
    [InlineData(SetupType.Propagation)]
    public void IsCompatible_AllowsB2aSetupTypesInMultiPurposeTent(SetupType setupType)
    {
        Assert.True(SetupTentCompatibilityPolicy.IsCompatible(TentType.MultiPurpose, setupType));
    }

    [Theory]
    [InlineData(SetupType.Production)]
    [InlineData(SetupType.Mother)]
    [InlineData(SetupType.Quarantine)]
    [InlineData(SetupType.Propagation)]
    public void IsCompatible_TakesNoSetupInDryingTent(SetupType setupType)
    {
        Assert.False(SetupTentCompatibilityPolicy.IsCompatible(TentType.Drying, setupType));
    }
}
