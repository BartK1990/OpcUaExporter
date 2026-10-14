# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

OPC UA Exporter is a Windows desktop app (WPF host + Blazor Hybrid UI) that connects to OPC UA servers, browses their address space, reads/writes/subscribes to live tag values, and exports selected tags to CSV or JSON.

OPC UA communication uses the native **OPCFoundation UA-.NETStandard** SDK directly from C# (`OpcUaClientService.cs`) — there is no Python subprocess or embedded runtime. [README.md](README.md) is kept in sync with this architecture.

The solution has two hosts over the same UI — the shipped WPF desktop shell and a cross-platform browser host used for development and end-to-end tests — plus a simulation OPC UA server and two test projects, described under "Architecture" below.

## Commands

Build and run:
```bash
dotnet build OpcUaExporter.sln
dotnet run --project src/OpcUaExporter.Wpf
```

Run the UI in a browser instead of WPF (any OS), optionally against the bundled simulation server:
```bash
dotnet run --project tools/OpcUaExporter.SimServer      # opc.tcp://localhost:4840
dotnet run --project src/OpcUaExporter.Web              # http://localhost:5080
```

Test (runs on any OS — neither test project references the WPF shell):
```bash
dotnet test tests/OpcUaExporter.Tests       # unit tests
dotnet test tests/OpcUaExporter.E2ETests    # Playwright/Chromium UI tests against the sim server
```
The E2E tests need a Playwright Chromium build (`PLAYWRIGHT_BROWSERS_PATH`, or `pwsh tests/OpcUaExporter.E2ETests/bin/Debug/net8.0/playwright.ps1 install chromium`; in cloud sessions it is preinstalled at `/opt/pw-browsers`).

Publish (self-contained; always publish the WPF shell, never a library):
```bash
dotnet publish src/OpcUaExporter.Wpf -c Release -r win-x64 --self-contained true
```

Or open `OpcUaExporter.sln` in Visual Studio 2022 and press F5.

The unit tests cover the platform-independent pieces (models, the diagnostics buffer, path layout, DI wiring). UI and live-session behaviour is covered by `tests/OpcUaExporter.E2ETests`, which start `OpcUaExporter.SimServer` and `OpcUaExporter.Web` in-process and drive the real pages in headless Chromium. When changing UI, add or extend an E2E test, and look at the result (Playwright screenshot) rather than assuming CSS/markup changes render as intended. Only the WPF shell itself is untested — keep it thin.

**`OpcUaExporter.Wpf` is `net8.0-windows` and cannot build on Linux (including cloud/CI sessions without Windows) — `dotnet build OpcUaExporter.sln` will always fail there on that project.** In a non-Windows session, build/test the other projects (`OpcUaExporter.Core`, `OpcUaExporter.UI`, `OpcUaExporter.Web`, `tools/OpcUaExporter.SimServer`, both test projects) individually instead of the whole solution, and don't try to "fix" the Wpf build failure — it's expected and unrelated to code correctness.

## Architecture

```
src/OpcUaExporter.Wpf    (net8.0-windows, WinExe → OpcUaExporter.exe)   src/OpcUaExporter.Web  (net8.0, browser host)
      │  references                                                       │
      └──────────────────────────────┬──────────────────────────────────────┘
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

- **AppPaths.cs** — the only place that knows about `%LocalAppData%\OpcUaExporter\` (overridable with the `OPCUAEXPORTER_DATA_DIR` environment variable, which the E2E tests use). Everything persisted (`pki\`, `crash.log`, `app-settings.json`, `last-profile.txt`) is addressed through it; add new persisted state here rather than recomputing the directory.
- **ServiceCollectionExtensions.cs** — `AddOpcUaExporterCore()`, the single registration point for the services below. All singletons so Blazor components share state; uses `TryAdd*` so a host can pre-register its own implementation of an abstraction.
- **Abstractions/IFileDialogService.cs** — native save/open/confirm/message dialogs. Blazor Hybrid has no browser file picker, so the shell supplies these. `NullFileDialogService` is the default and cancels every prompt (component tests, previews).
- **Services/OpcUaClientService.cs** — the OPC UA client itself: builds the `ApplicationConfiguration` (client cert under `AppPaths.PkiDirectory`), discovers endpoints, selects one matching the requested security mode/policy, creates sessions, browses the address space (recursively, optionally in parallel — see `ConnectionProfile.EnableParallelBrowse`/`ParallelBrowseMaxDegree`), reads/writes node values, manages subscriptions (`Opc.Ua.Client.Subscription`/`MonitoredItem`), and does TCP+discovery port scanning for server discovery. Untrusted server certificates are captured into a pending list (`_pendingCertificates`) rather than auto-accepted or auto-rejected — the UI surfaces them for the user to trust/reject.
- **Services/OpcUaService.cs** — the stateful façade Blazor components bind to. Owns `ConnectionProfile`, the browsed `TagTree`, `LastReadings`, busy/status flags, and fires a `StateChanged` event that pages subscribe to for re-rendering (this is a manual pub/sub, not Blazor's built-in state binding — pages call `Opc.StateChanged += StateHasChanged` in `OnInitialized` and unsubscribe in `Dispose`). Also owns cross-cutting live-data features layered on top of one subscription: CSV recording (wide-format, one column per subscribed tag) and the live trend chart's node selection. All public async operations are wrapped through the private `RunSafe` helper, which sets `IsBusy`/`StatusMessage`/`HasError` uniformly and swallows `OperationCanceledException` into a status message.
- **Services/DiagnosticsLogService.cs** — a bounded (500-entry) ring buffer of timestamped diagnostic strings, written to by `OpcUaClientService` during connect/browse/etc., displayed on the Diagnostics page.
- **Services/ThemeService.cs** — light/dark theme flag persisted to `AppPaths.SettingsFile`.
- **Models/OpcModels.cs** — all DTOs/enums in one file: `OpcTag` (tree node, `IsSelectable` = is a Variable), `TagReading`, `ConnectionProfile`, `NodeDetails`/`NodeAttributeInfo`/`NodeReferenceInfo` (node-properties panel), `ServerCapabilitiesInfo`/`ServerSecurityOption` (security discovery), `DiscoveredServerInfo` (port scan result), `ExportOptions`.

### OpcUaExporter.Web — the browser host (dev/test)

- **OpcUaExporterWebApp.cs** — builds the ASP.NET Core app (Blazor Web App, interactive server, prerendering off) that renders the same `Routes` component; `Program.cs` just runs it, and the E2E tests start it in-process on a free port. Static web assets are enabled in every environment so `_content/OpcUaExporter.UI/...` resolves from any entry assembly.
- **Components/App.razor** — host page; the counterpart of the WPF `wwwroot/index.html`. **Keep their `<link>`/`<script>` lists in sync.**
- **Services/BrowserFileDialogService.cs** — `IFileDialogService` over the browser's `prompt`/`confirm`/`alert`. Registered scoped (it needs the circuit's `IJSRuntime`); nothing singleton may depend on `IFileDialogService`.
- Local, single-user host: services stay singletons, so all tabs share one connection like the desktop window. Not meant for deployment.

### tools/OpcUaExporter.SimServer and tests/OpcUaExporter.E2ETests

- **SimServer** — OPC UA server (`SimServerHost.StartAsync(port)`) with `Objects/Simulation/{Dynamic,Static,Deep}`: once-a-second changing values, writable tags of every type in the UI's write list (plus a `BaseDataType` one that needs the manual type override and a read-only one), and a 5-level branch. Extend it when a feature needs a node shape it doesn't have yet.
- **E2ETests** — `E2EEnvironment` (collection fixture) owns the sim server, Chromium and an isolated `OPCUAEXPORTER_DATA_DIR`; `UiTestBase` gives each test a fresh web app + page and fails it on any page error or HTTP ≥ 400. Helpers: `BrowseSimServerAsync`, `ExpandAsync`, `SelectTagsAsync`, `TreeRow`, `SelectedRow`.

### OpcUaExporter.Wpf — the shell

- **App.xaml.cs** — WPF entry point. Builds the DI container (registers `WpfFileDialogService` first, then calls `AddOpcUaExporterUi()`) and hooks global unhandled-exception handlers that log to `AppPaths.CrashLogFile`.
- **MainWindow.xaml.cs** — hosts the `BlazorWebView` and assigns its `Services`. Deliberately thin; it has no `[JSInvokable]` methods.
- **Services/WpfFileDialogService.cs** — `IFileDialogService` over `Microsoft.Win32` dialogs, marshalled onto the dispatcher (Win32 common dialogs need an STA thread).
- **wwwroot/index.html** — the BlazorWebView host page (keep its asset list in sync with `OpcUaExporter.Web/Components/App.razor`). Its `<link>`/`<script>` tags point at `_content/OpcUaExporter.UI/...`, where the Razor SDK publishes the UI library's static assets. Keep it free of app logic.

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

`OpcUaService` keeps exactly **one** active subscription (`_activeSubscription`, an `ILiveSubscription`) shared by live readings, CSV recording, and the trend chart. Subscription changes are **queued, not awaited**: UI actions (`ToggleTagSubscribe`, `SubscribeSelected`, `TrendSelected`, `AddToTrend`, `StopSubscription`, deselecting a tag) only change the requested set via `ChangeDesiredSubscription` and return immediately; `RunSubscriptionSyncLoopAsync` debounces bursts, then reconciles the live subscription to the latest requested set — opening it, adding/removing monitored items in place (`ILiveSubscription.UpdateAsync`, no new session), or stopping it. A newer request supersedes one still debouncing; anything that must wait for the result (e.g. `StartRecordingAsync`) awaits `WaitForSubscriptionSyncAsync`. Trended tags are always a subset of the requested set. Recording and the trend chart both consume updates from the same `ApplySubscriptionUpdate` callback rather than having independent subscriptions — keep that invariant when touching this area.

Browsing is likewise background work: `BrowseAsync` does not set `IsBusy` (only `IsBrowsing`). The client builds the tree top-down and publishes each node's children as soon as they are fetched (`OpcTag.PublishChildren`), so the tree fills in live. Any node not scanned yet (`OpcTag.CanLoadChildren`) can be expanded on demand (`OpcUaService.LoadChildrenAsync` → `OpcUaClientService.BrowseChildrenAsync`, over a reused interactive session); whichever of the two browsers publishes a node's children first wins, and the other reuses those `OpcTag` instances.

## Project-specific conventions

- Modern, non-deprecated APIs only for OPC UA certificate handling (per `.github/copilot-instructions.md`) — when working in `OpcUaClientService.cs`'s certificate/security code, prefer current OPCFoundation SDK APIs over older/obsolete overloads.
- Services are DI singletons; new services that hold per-session state should follow the same pattern (register in `AddOpcUaExporterCore()`, inject into Razor pages with `@inject`) rather than introducing scoped/transient lifetimes that wouldn't fit the single-window app model. Only genuinely Windows-specific services are registered in `App.xaml.cs`.
- All OPC UA client/PKI/log/settings state lives under `%LocalAppData%\OpcUaExporter\` — address it through `AppPaths`, never by recomputing the directory.
- Anything the UI needs from the host goes through an abstraction in `OpcUaExporter.Core/Abstractions/`, implemented in **both** hosts: `OpcUaExporter.Wpf/Services/` and `OpcUaExporter.Web/Services/`. Do not reintroduce static `[JSInvokable]` bridges: they hard-code the host assembly name and cannot be substituted in tests.
- The UI library's static assets are addressed as `_content/OpcUaExporter.UI/...` from the host page. Renaming the UI assembly changes that path.
