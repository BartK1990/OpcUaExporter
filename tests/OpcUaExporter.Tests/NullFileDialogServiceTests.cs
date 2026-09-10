using OpcUaExporter.Abstractions;
using Xunit;

namespace OpcUaExporter.Tests;

public class NullFileDialogServiceTests
{
    private readonly NullFileDialogService _dialogs = new();

    [Fact]
    public async Task ShowSaveFileDialogAsync_ReportsCancellation()
    {
        Assert.Null(await _dialogs.ShowSaveFileDialogAsync("CSV Files|*.csv"));
    }

    [Fact]
    public async Task ShowOpenFileDialogAsync_ReportsCancellation()
    {
        Assert.Null(await _dialogs.ShowOpenFileDialogAsync("JSON Files|*.json"));
    }

    [Fact]
    public async Task ConfirmAsync_DoesNotConfirm()
    {
        Assert.False(await _dialogs.ConfirmAsync("Load last saved server profile?"));
    }
}
