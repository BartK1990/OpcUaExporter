using OpcUaExporter.Services;
using Xunit;

namespace OpcUaExporter.Tests;

public class DiagnosticsLogServiceTests
{
    private const int MaxEntries = 500;

    [Fact]
    public void Add_PrefixesTheEntryWithATimestamp()
    {
        var log = new DiagnosticsLogService();

        log.Add("connected");

        var entry = Assert.Single(log.Entries);
        Assert.Matches(@"^\[\d{2}:\d{2}:\d{2}\] connected$", entry);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t\n")]
    public void Add_IgnoresBlankMessages(string message)
    {
        var log = new DiagnosticsLogService();

        log.Add(message);

        Assert.Empty(log.Entries);
    }

    [Fact]
    public void Add_KeepsOnlyTheMostRecentEntriesOnceTheBufferIsFull()
    {
        var log = new DiagnosticsLogService();

        for (var i = 0; i < MaxEntries + 25; i++)
            log.Add($"entry {i}");

        Assert.Equal(MaxEntries, log.Entries.Count);
        Assert.EndsWith($"entry {MaxEntries + 24}", log.Entries[^1]);
        Assert.EndsWith("entry 25", log.Entries[0]);
    }

    [Fact]
    public void Add_RaisesChanged()
    {
        var log = new DiagnosticsLogService();
        var raised = 0;
        log.Changed += () => raised++;

        log.Add("first");
        log.Add("second");

        Assert.Equal(2, raised);
    }

    [Fact]
    public void Clear_EmptiesTheBufferAndRaisesChanged()
    {
        var log = new DiagnosticsLogService();
        log.Add("noise");
        var raised = 0;
        log.Changed += () => raised++;

        log.Clear();

        Assert.Empty(log.Entries);
        Assert.Equal(1, raised);
    }

    [Fact]
    public void Entries_IsASnapshotAndNotAffectedByLaterWrites()
    {
        var log = new DiagnosticsLogService();
        log.Add("first");

        var snapshot = log.Entries;
        log.Add("second");

        Assert.Single(snapshot);
        Assert.Equal(2, log.Entries.Count);
    }
}
