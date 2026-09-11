using System.Windows;

namespace OpcUaExporter;

/// <summary>
/// WPF host window for the Blazor Hybrid UI.
/// <para>
/// The window is deliberately thin: it owns the <c>BlazorWebView</c> and
/// nothing else. Platform services the UI needs (native dialogs, message
/// boxes) are injected into the Blazor components through
/// <see cref="Abstractions.IFileDialogService"/> instead of being reached via
/// static <c>[JSInvokable]</c> methods on this class.
/// </para>
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        InitializeComponent();

        BlazorWebView.Services = services;
    }
}
