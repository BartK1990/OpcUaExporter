# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

OPC UA Exporter is a Windows desktop app (WPF host + Blazor Hybrid UI) that connects to OPC UA servers, browses their address space, reads/writes/subscribes to live tag values, and exports selected tags to CSV or JSON.

OPC UA communication uses the native **OPCFoundation UA-.NETStandard** SDK directly from C# (`OpcUaClientService.cs`) — there is no Python subprocess or embedded runtime. [README.md](README.md) is kept in sync with this architecture.

The solution has four projects (`src/` + `tests/`), described under "Architecture" below.

## Commands

Build and run:
```bash
dotnet build OpcUaExporter.sln
dotnet run --project src/OpcUaExporter.Wpf
```

Test (runs on any OS — the test project references only the `net8.0` projects):
```bash
dotnet test tests/OpcUaExporter.Tests
```

Publish (self-contained; always publish the WPF shell, never a library):
```bash
dotnet publish src/OpcUaExporter.Wpf -c Release -r win-x64 --self-contained true
```

Or open `OpcUaExporter.sln` in Visual Studio 2022 and press F5.

The unit tests cover only the platform-independent pieces (models, the diagnostics buffer, path layout, DI wiring). Anything touching a live OPC UA session or the WebView still has to be verified by running the app and exercising the relevant page (see "UI structure" below).

## Architecture

```
src/OpcUaExporter.Wpf    (net8.0-windows, WinExe → OpcUaExporter.exe)
      │  references
src/OpcUaExporter.UI     (net8.0, Razor class library)
      │  references
src/OpcUaExporter.Core   (net8.0, class library)
      │
OPCFoundation.NetStandard.Opc.Ua.Client → OPC UA Server (network)
```

The dependency chain runs one way only. **Core and UI must never take a WPF or
Windows-only dependency** — that is what keeps them buildable and testable off
Windows. When the UI needs something only the host can do, declare an
abstraction in `Core/Abstractions/` and implement it in the WPF project.

### OpcUaExporter.Core — models and services

- **AppPaths.cs** — the only place that knows about `%LocalAppData%\OpcUaExporter\`. Everything persisted (`pki\`, `crash.log`, `app-settings.json`, `last-profile.txt`) is addressed through it; add new persisted state here rather than recomputing the directory.
- **ServiceCollectionExtensions.cs** — `AddOpcUaExporterCore()`, the single registration point for the services below. All singletons so Blazor components share state; uses `TryAdd*` so a host can pre-register its own implementation of an abstraction.
- **Abstractions/IFileDialogService.cs** — native save/open/confirm/message dialogs. Blazor Hybrid has no browser file picker, so the shell supplies these. `NullFileDialogService` is the default and cancels every prompt (component tests, previews).
- **Services/OpcUaClientService.cs** — the OPC UA client itself: builds the `ApplicationConfiguration` (client cert under `AppPaths.PkiDirectory`), discovers endpoints, selects one matching the requested security mode/policy, creates sessions, browses the address space (recursively, optionally in parallel — see `ConnectionProfile.EnableParallelBrowse`/`ParallelBrowseMaxDegree`), reads/writes node values, manages subscriptions (`Opc.Ua.Client.Subscription`/`MonitoredItem`), and does TCP+discovery port scanning for server discovery. Untrusted server certificates are captured into a pending list (`_pendingCertificates`) rather than auto-accepted or auto-rejected — the UI surfaces them for the user to trust/reject.
- **Services/OpcUaService.cs** — the stateful façade Blazor components bind to. Owns `ConnectionProfile`, the browsed `TagTree`, `LastReadings`, busy/status flags, and fires a `StateChanged` event that pages subscribe to for re-rendering (this is a manual pub/sub, not Blazor's built-in state binding — pages call `Opc.StateChanged += StateHasChanged` in `OnInitialized` and unsubscribe in `Dispose`). Also owns cross-cutting live-data features layered on top of one subscription: CSV recording (wide-format, one column per subscribed tag) and the live trend chart's node selection. All public async operations are wrapped through the private `RunSafe` helper, which sets `IsBusy`/`StatusMessage`/`HasError` uniformly and swallows `OperationCanceledException` into a status message.
- **Services/DiagnosticsLogService.cs** — a bounded (500-entry) ring buffer of timestamped diagnostic strings, written to by `OpcUaClientService` during connect/browse/etc., displayed on the Diagnostics page.
- **Services/ThemeService.cs** — light/dark theme flag persisted to `AppPaths.SettingsFile`.
- **Models/OpcModels.cs** — all DTOs/enums in one file: `OpcTag` (tree node, `IsSelectable` = is a Variable), `TagReading`, `ConnectionProfile`, `NodeDetails`/`NodeAttributeInfo`/`NodeReferenceInfo` (node-properties panel), `ServerCapabilitiesInfo`/`ServerSecurityOption` (security discovery), `DiscoveredServerInfo` (port scan result), `ExportOptions`.

### OpcUaExporter.Wpf — the shell

- **App.xaml.cs** — WPF entry point. Builds the DI container (registers `WpfFileDialogService` first, then calls `AddOpcUaExporterUi()`) and hooks global unhandled-exception handlers that log to `AppPaths.CrashLogFile`.
- **MainWindow.xaml.cs** — hosts the `BlazorWebView` and assigns its `Services`. Deliberately thin; it has no `[JSInvokable]` methods.
- **Services/WpfFileDialogService.cs** — `IFileDialogService` over `Microsoft.Win32` dialogs, marshalled onto the dispatcher (Win32 common dialogs need an STA thread).
- **wwwroot/index.html** — the BlazorWebView host page. Its `<link>`/`<script>` tags point at `_content/OpcUaExporter.UI/...`, where the Razor SDK publishes the UI library's static assets. Keep it free of app logic.

### UI structure (src/OpcUaExporter.UI/Components/)

- **Routes.razor** — the Blazor router mounted as the WebView's root component (named `Routes`, not `App`, so it isn't confused with the WPF `App`).
- **Pages/Index.razor** — main tag-browser page: sidebar (connection form + certificate trust UI) + tag tree (`TagNode.razor`, recursive) + readings table + CSV/JSON export + recording controls + live trend chart. Largest file in the project; most feature wiring happens here.
- **Pages/ConnectionSettings.razor** — profile save/load, security mode/policy, authentication.
- **Pages/ServerDiscovery.razor** — host/port scanning UI driven by `OpcUaService.QuickScanAsync`/`FullScanAsync`/`CustomScanAsync`.
- **Pages/Diagnostics.razor** — renders `DiagnosticsLogService.Entries`.
- **TagNode.razor** — recursive tree node component for the tag browser.
- **ThemeToggle.razor** — light/dark switch bound to `ThemeService`.
- **wwwroot/js/** — small JS interop helpers referenced by the host page: `resizable-panes.js` and `resizable-columns.js` (drag-resize for the layout), `trend-chart.js` (renders the live trend chart; fed via JSInterop from `Index.razor`, driven by `OpcUaService.TrendUpdate`), `theme.js` (`appTheme.set`, applies `data-theme` to the document). File dialogs are **not** JS interop — inject `IFileDialogService` instead.

### Key flows to know before changing subscription/recording/trend code

`OpcUaService` keeps exactly **one** active subscription (`_activeSubscription`) shared by live readings, CSV recording, and the trend chart. Selecting a different tag set for recording or trending will resubscribe (`SubscribeToAsync` tears down and recreates the subscription) if the requested set differs from `_subscribedNodeIds`. Recording and the trend chart both consume updates from the same `ApplySubscriptionUpdate` callback rather than having independent subscriptions — keep that invariant when touching this area.

## Project-specific conventions

- Modern, non-deprecated APIs only for OPC UA certificate handling (per `.github/copilot-instructions.md`) — when working in `OpcUaClientService.cs`'s certificate/security code, prefer current OPCFoundation SDK APIs over older/obsolete overloads.
- Services are DI singletons; new services that hold per-session state should follow the same pattern (register in `AddOpcUaExporterCore()`, inject into Razor pages with `@inject`) rather than introducing scoped/transient lifetimes that wouldn't fit the single-window app model. Only genuinely Windows-specific services are registered in `App.xaml.cs`.
- All OPC UA client/PKI/log/settings state lives under `%LocalAppData%\OpcUaExporter\` — address it through `AppPaths`, never by recomputing the directory.
- Anything the UI needs from the host goes through an abstraction in `OpcUaExporter.Core/Abstractions/`, implemented in `OpcUaExporter.Wpf/Services/`. Do not reintroduce static `[JSInvokable]` bridges: they hard-code the host assembly name and cannot be substituted in tests.
- The UI library's static assets are addressed as `_content/OpcUaExporter.UI/...` from the host page. Renaming the UI assembly changes that path.
