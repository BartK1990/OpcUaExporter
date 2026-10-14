using Opc.Ua;
using OpcUaBridge.Acquisition;
using OpcUaBridge.Configuration;
using Xunit;

namespace OpcUaBridge.Tests;

public sealed class DataChangeFilterTests
{
    private static AcquisitionOptions WithDeadband(AcquisitionDeadband type, double value)
        => new() { Deadband = type, DeadbandValue = value };

    [Fact]
    public void BuildDataChangeFilter_ByDefault_ReportsEveryChange()
    {
        // The bridge mirrors what the upstream server says. Filtering is a deliberate
        // decision to stop doing that exactly, so it is never on by default.
        Assert.Null(SubscriptionAcquisitionEngine.BuildDataChangeFilter(new AcquisitionOptions()));
    }

    [Theory]
    [InlineData(AcquisitionDeadband.Absolute)]
    [InlineData(AcquisitionDeadband.Percent)]
    public void BuildDataChangeFilter_WithAZeroMagnitude_ReportsEveryChange(AcquisitionDeadband type)
    {
        Assert.Null(SubscriptionAcquisitionEngine.BuildDataChangeFilter(WithDeadband(type, 0)));
    }

    [Fact]
    public void BuildDataChangeFilter_Absolute_AsksTheServerForAnAbsoluteDeadband()
    {
        var filter = SubscriptionAcquisitionEngine.BuildDataChangeFilter(
            WithDeadband(AcquisitionDeadband.Absolute, 0.5));

        Assert.NotNull(filter);
        Assert.Equal((uint)DeadbandType.Absolute, filter.DeadbandType);
        Assert.Equal(0.5, filter.DeadbandValue);
    }

    [Fact]
    public void BuildDataChangeFilter_Percent_AsksTheServerForAPercentDeadband()
    {
        var filter = SubscriptionAcquisitionEngine.BuildDataChangeFilter(
            WithDeadband(AcquisitionDeadband.Percent, 2));

        Assert.NotNull(filter);
        Assert.Equal((uint)DeadbandType.Percent, filter.DeadbandType);
        Assert.Equal(2, filter.DeadbandValue);
    }

    [Fact]
    public void BuildDataChangeFilter_AlwaysTriggersOnStatusAsWellAsValue()
    {
        var filter = SubscriptionAcquisitionEngine.BuildDataChangeFilter(
            WithDeadband(AcquisitionDeadband.Absolute, 10));

        // A tag going bad has to reach the downstream application even when its number
        // has not moved. A gateway that swallowed that would be worse than useless.
        Assert.NotNull(filter);
        Assert.Equal(DataChangeTrigger.StatusValue, filter.Trigger);
    }

    [Fact]
    public void Validate_RejectsADeadbandTypeWithNoMagnitude()
    {
        var options = new BridgeOptions();
        options.Acquisition.Deadband = AcquisitionDeadband.Absolute;

        var failures = new BridgeOptionsValidator().Validate(null, options).Failures ?? [];

        Assert.Contains(failures, f => f.Contains("DeadbandValue"));
    }

    [Fact]
    public void Validate_RejectsAPercentDeadbandAboveOneHundred()
    {
        var options = new BridgeOptions();
        options.Acquisition.Deadband = AcquisitionDeadband.Percent;
        options.Acquisition.DeadbandValue = 150;

        var failures = new BridgeOptionsValidator().Validate(null, options).Failures ?? [];

        Assert.Contains(failures, f => f.Contains("cannot exceed 100"));
    }

    [Fact]
    public void Validate_AcceptsADeadbandInPollingMode()
    {
        // Inert, not harmful. A gateway whose job is to be available should not decline to
        // start over a setting that does nothing; the polling engine warns about it instead.
        var options = new BridgeOptions();
        options.Acquisition.Mode = AcquisitionMode.Polling;
        options.Acquisition.Deadband = AcquisitionDeadband.Absolute;
        options.Acquisition.DeadbandValue = 1;

        Assert.True(new BridgeOptionsValidator().Validate(null, options).Succeeded);
    }
}
