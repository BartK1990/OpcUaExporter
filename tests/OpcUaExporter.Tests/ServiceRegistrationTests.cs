using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpcUaExporter.Abstractions;
using OpcUaExporter.Services;
using OpcUaExporter.UI;
using Xunit;

namespace OpcUaExporter.Tests;

/// <summary>
/// Guards the composition root. The pages assume every service is a shared
/// singleton — a lifetime slip here would silently give each page its own
/// connection and tag tree.
/// </summary>
public class ServiceRegistrationTests
{
    [Theory]
    [InlineData(typeof(DiagnosticsLogService))]
    [InlineData(typeof(OpcUaClientService))]
    [InlineData(typeof(OpcUaService))]
    [InlineData(typeof(ThemeService))]
    [InlineData(typeof(IFileDialogService))]
    public void AddOpcUaExporterCore_ResolvesTheServiceAsASingleton(Type serviceType)
    {
        using var provider = BuildProvider(services => services.AddOpcUaExporterCore());

        var first = provider.GetRequiredService(serviceType);
        var second = provider.GetRequiredService(serviceType);

        Assert.Same(first, second);
    }

    [Fact]
    public void AddOpcUaExporterUi_AlsoRegistersTheCoreServices()
    {
        using var provider = BuildProvider(services => services.AddOpcUaExporterUi());

        Assert.NotNull(provider.GetRequiredService<OpcUaService>());
    }

    [Fact]
    public void FileDialogService_DefaultsToTheNoOpImplementation()
    {
        using var provider = BuildProvider(services => services.AddOpcUaExporterCore());

        Assert.IsType<NullFileDialogService>(provider.GetRequiredService<IFileDialogService>());
    }

    [Fact]
    public void FileDialogService_RegisteredByTheHostWins()
    {
        using var provider = BuildProvider(services =>
        {
            services.AddSingleton<IFileDialogService, StubFileDialogService>();
            services.AddOpcUaExporterCore();
        });

        Assert.IsType<StubFileDialogService>(provider.GetRequiredService<IFileDialogService>());
    }

    private static ServiceProvider BuildProvider(Action<IServiceCollection> configure)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        configure(services);
        return services.BuildServiceProvider();
    }

    private sealed class StubFileDialogService : IFileDialogService
    {
        public Task<string?> ShowSaveFileDialogAsync(string filter) => Task.FromResult<string?>("save.csv");

        public Task<string?> ShowOpenFileDialogAsync(string filter) => Task.FromResult<string?>("open.json");

        public Task<bool> ConfirmAsync(string message) => Task.FromResult(true);

        public Task ShowMessageAsync(string message) => Task.CompletedTask;
    }
}
