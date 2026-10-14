using Microsoft.Extensions.Logging.Abstractions;
using OpcUaExporter.Models;
using OpcUaExporter.Services;
using Xunit;

namespace OpcUaExporter.Tests;

/// <summary>CSV recording writes one row per interval, stamped with whole seconds and never duplicated.</summary>
public class OpcUaServiceRecordingTests
{
    [Theory]
    [InlineData("2026-10-13T12:34:56.789Z", 1, "2026-10-13T12:34:56Z")]
    [InlineData("2026-10-13T12:34:56.001Z", 1, "2026-10-13T12:34:56Z")]
    [InlineData("2026-10-13T12:34:59.999Z", 5, "2026-10-13T12:34:55Z")]
    [InlineData("2026-10-13T12:35:00.000Z", 5, "2026-10-13T12:35:00Z")]
    [InlineData("2026-10-13T12:34:56.500Z", 60, "2026-10-13T12:34:00Z")]
    public void RecordingSlot_IsWholeSecondsAlignedToInterval(string now, int interval, string expected)
    {
        var utc = DateTime.Parse(now, null, System.Globalization.DateTimeStyles.AdjustToUniversal);

        var slot = OpcUaService.RecordingSlot(utc, interval);

        Assert.Equal(expected, OpcUaService.FormatRecordingTimestamp(slot));
    }

    [Fact]
    public void RecordingInterval_DefaultsToOneSecond_AndIsClamped()
    {
        var service = new OpcUaService(
            new OpcUaClientService(NullLogger<OpcUaClientService>.Instance, new DiagnosticsLogService()),
            NullLogger<OpcUaService>.Instance,
            new DiagnosticsLogService());

        Assert.Equal(1, service.RecordingIntervalSeconds);

        service.RecordingIntervalSeconds = 0;
        Assert.Equal(1, service.RecordingIntervalSeconds);

        service.RecordingIntervalSeconds = 100_000;
        Assert.Equal(OpcUaService.MaxRecordingIntervalSeconds, service.RecordingIntervalSeconds);
    }

    [Fact]
    public void WriteRecordingSample_SkipsSlotsAlreadyWritten()
    {
        var service = new OpcUaService(
            new OpcUaClientService(NullLogger<OpcUaClientService>.Instance, new DiagnosticsLogService()),
            NullLogger<OpcUaService>.Instance,
            new DiagnosticsLogService());
        var stream = new StreamWriter(new MemoryStream());
        SetField(service, "_recordingWriter", stream);
        SetField(service, "_recordingNodeIds", new List<string> { "ns=2;s=A" });
        var latest = (Dictionary<string, TagReading>)GetField(service, "_recordingLatestByNodeId");
        latest["ns=2;s=A"] = new TagReading { NodeId = "ns=2;s=A", Value = 42, Quality = "Good" };

        var t0 = new DateTime(2026, 10, 13, 12, 0, 0, DateTimeKind.Utc);
        Assert.True(service.WriteRecordingSample(t0.AddMilliseconds(10)));
        Assert.False(service.WriteRecordingSample(t0.AddMilliseconds(900)));
        Assert.True(service.WriteRecordingSample(t0.AddSeconds(1).AddMilliseconds(5)));
        Assert.False(service.WriteRecordingSample(t0.AddMilliseconds(500)));

        stream.Flush();
        stream.BaseStream.Position = 0;
        var lines = new StreamReader(stream.BaseStream).ReadToEnd()
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(["2026-10-13T12:00:00Z,42", "2026-10-13T12:00:01Z,42"], lines);
        Assert.Equal(2, service.RecordedRowCount);
    }

    private static void SetField(object target, string name, object value)
        => target.GetType().GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(target, value);

    private static object GetField(object target, string name)
        => target.GetType().GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(target)!;
}
