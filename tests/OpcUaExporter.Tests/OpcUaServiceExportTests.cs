using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using OpcUaExporter.Models;
using OpcUaExporter.Services;
using Xunit;

namespace OpcUaExporter.Tests;

/// <summary>Exporting the selected tags, with and without their last read values.</summary>
public sealed class OpcUaServiceExportTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("opcua-export-").FullName;
    private readonly OpcUaService _service;

    public OpcUaServiceExportTests()
    {
        var read     = new OpcTag { NodeId = "ns=2;s=Read", DisplayName = "Read", NodeClass = "Variable", DataType = "Variant", IsSelected = true };
        var unread   = new OpcTag { NodeId = "ns=2;s=Unread", DisplayName = "Unread", NodeClass = "Variable", DataType = "Int32", IsSelected = true };
        var failed   = new OpcTag { NodeId = "ns=2;s=Failed", DisplayName = "Failed", NodeClass = "Variable", DataType = "Double", IsSelected = true };
        var array    = new OpcTag { NodeId = "ns=2;s=Array", DisplayName = "Array, with comma", NodeClass = "Variable", DataType = "Double", IsSelected = true };
        var folder   = new OpcTag { NodeId = "ns=2;s=Folder", DisplayName = "Folder", NodeClass = "Object", Children = [read, unread, failed, array] };

        _service = new OpcUaService(
            new OpcUaClientService(NullLogger<OpcUaClientService>.Instance, new DiagnosticsLogService()),
            NullLogger<OpcUaService>.Instance,
            new DiagnosticsLogService());
        // Both have private setters: normally filled by browsing and reading a live server.
        typeof(OpcUaService).GetProperty(nameof(OpcUaService.TagTree))!.SetValue(_service, new List<OpcTag> { folder });
        typeof(OpcUaService).GetProperty(nameof(OpcUaService.LastReadings))!.SetValue(_service, new List<TagReading>
        {
            new() { NodeId = "ns=2;s=Read", Value = 1.5, DataType = "Double", Quality = "Good", Timestamp = "2026-11-24 10:00:00" },
            new() { NodeId = "ns=2;s=Failed", Error = "BadNodeIdUnknown" },
            new() { NodeId = "ns=2;s=Array", Value = new[] { 1.25, 2.5 }, DataType = "Double", Quality = "Good", Timestamp = "2026-11-24 10:00:01" },
        });
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public async Task Csv_WithoutValues_KeepsTheNamespaceOnlyLayout()
    {
        var lines = await ExportLinesAsync(ExportFormat.Csv, includeValues: false);

        Assert.Equal("Display Name,Node ID,Data Type", lines[0]);
        Assert.Contains("Read,ns=2;s=Read,Variant", lines);
        Assert.Contains("Unread,ns=2;s=Unread,Int32", lines);
    }

    [Fact]
    public async Task Csv_WithValues_AddsLastValueColumns_AndLeavesUnreadTagsEmpty()
    {
        var lines = await ExportLinesAsync(ExportFormat.Csv, includeValues: true);

        Assert.Equal("Display Name,Node ID,Data Type,Value,Quality,Timestamp", lines[0]);
        // The read refines the declared Variant to the real type.
        Assert.Contains("Read,ns=2;s=Read,Double,1.5,Good,2026-11-24 10:00:00", lines);
        Assert.Contains("Unread,ns=2;s=Unread,Int32,,,", lines);
        Assert.Contains("Failed,ns=2;s=Failed,Double,,BadNodeIdUnknown,", lines);
        Assert.Contains("\"Array, with comma\",ns=2;s=Array,Double,\"[1.25, 2.5]\",Good,2026-11-24 10:00:01", lines);
        Assert.Equal(5, lines.Length);
    }

    [Fact]
    public async Task Json_WithValues_IncludesValueOnlyWhereOneWasRead()
    {
        var path = Path.Combine(_dir, "tags.json");
        await _service.ExportAsync(new ExportOptions { OutputPath = path, Format = ExportFormat.Json, IncludeLastValues = true });

        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        var rows = doc.RootElement.EnumerateArray().ToDictionary(e => e.GetProperty("NodeId").GetString()!);

        Assert.Equal("1.5", rows["ns=2;s=Read"].GetProperty("Value").GetString());
        Assert.Equal("Good", rows["ns=2;s=Read"].GetProperty("Quality").GetString());
        Assert.False(rows["ns=2;s=Unread"].TryGetProperty("Value", out _));
    }

    [Fact]
    public async Task Json_WithoutValues_OmitsValueFields()
    {
        var path = Path.Combine(_dir, "tags.json");
        await _service.ExportAsync(new ExportOptions { OutputPath = path, Format = ExportFormat.Json });

        var json = await File.ReadAllTextAsync(path);
        Assert.DoesNotContain("\"Value\"", json);
        Assert.DoesNotContain("\"Quality\"", json);
    }

    [Theory]
    [InlineData(ExportFormat.Xlsx)]
    public async Task Xlsx_WithValues_WritesAFile(ExportFormat format)
    {
        var path = Path.Combine(_dir, "tags.xlsx");
        await _service.ExportAsync(new ExportOptions { OutputPath = path, Format = format, IncludeLastValues = true });

        Assert.False(_service.HasError, _service.StatusMessage);
        Assert.True(new FileInfo(path).Length > 0);
    }

    [Fact]
    public void FormatExportValue_IsCultureIndependent()
    {
        var previous = Thread.CurrentThread.CurrentCulture;
        Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
        try
        {
            Assert.Equal("1.5", OpcUaService.FormatExportValue(1.5));
            Assert.Equal("2026-11-24 10:00:00", OpcUaService.FormatExportValue(new DateTime(2026, 11, 24, 10, 0, 0, DateTimeKind.Utc)));
            Assert.Equal("[True, False]", OpcUaService.FormatExportValue(new[] { true, false }));
            Assert.Equal("AQI=", OpcUaService.FormatExportValue(new byte[] { 1, 2 }));
            Assert.Null(OpcUaService.FormatExportValue(null));
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previous;
        }
    }

    private async Task<string[]> ExportLinesAsync(ExportFormat format, bool includeValues)
    {
        var path = Path.Combine(_dir, "tags.csv");
        await _service.ExportAsync(new ExportOptions { OutputPath = path, Format = format, IncludeLastValues = includeValues });
        Assert.False(_service.HasError, _service.StatusMessage);
        return (await File.ReadAllTextAsync(path)).Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
    }
}
