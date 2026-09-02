# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

OPC UA Exporter is a Windows desktop app (WPF host + Blazor Hybrid UI) that connects to OPC UA servers, browses their address space, reads/writes/subscribes to live tag values, and exports selected tags to CSV or JSON. There is a single project, no test project, no solution-wide build config beyond Debug/Release.

OPC UA communication uses the native **OPCFoundation UA-.NETStandard** SDK directly from C# (`OpcUaClientService.cs`) — there is no Python subprocess or embedded runtime. [README.md](README.md) is kept in sync with this architecture.

## Commands

Build and run (from repo root or `OpcUaExporter/`):
```bash
dotnet build
dotnet run --project OpcUaExporter
```

Or open `OpcUaExporter.sln` in Visual Studio 2022 and press F5.

Publish (self-contained, single-project — no extra `Python\` folder to copy anymore):
```bash
dotnet publish -c Release -r win-x64 --self-contained true
```

There are no automated tests in this repo. Verify changes by running the app and exercising the relevant page in the UI (see "UI structure" below).

## Architecture

```
MainWindow.xaml (WPF) → BlazorWebView → Razor pages/components
                                              │ DI
                                       OpcUaService (state/orchestration)
                                              │
                                     OpcUaClientService (OPC UA session/browse/read/write/subscribe)
                                              │
                                   OPCFoundation.NetStandard.Opc.Ua.Client
                                              │
                                       OPC UA Server (network)
```

- **App.xaml.cs** — WPF entry point. Builds the DI container (all app services registered as **singletons** so Blazor components share state) and hooks global unhandled-exception handlers that log to `%LocalAppData%\OpcUaExporter\crash.log`.
- **MainWindow.xaml.cs** — hosts the `BlazorWebView` and exposes JS-invokable native dialogs (`ShowSaveDialogAsync`, `ShowOpenDialogAsync`, `ConfirmAsync`) since Blazor Hybrid has no browser file-picker.
- **Services/OpcUaClientService.cs** — the OPC UA client itself: builds the `ApplicationConfiguration` (with client cert stored under `%LocalAppData%\OpcUaExporter\pki\`), discovers endpoints, selects one matching the requested security mode/policy, creates sessions, browses the address space (recursively, optionally in parallel — see `ConnectionProfile.EnableParallelBrowse`/`ParallelBrowseMaxDegree`), reads/writes node values, manages subscriptions (`Opc.Ua.Client.Subscription`/`MonitoredItem`), and does TCP+discovery port scanning for server discovery. Untrusted server certificates are captured into a pending list (`_pendingCertificates`) rather than auto-accepted or auto-rejected — the UI surfaces them for the user to trust/reject.
- **Services/OpcUaService.cs** — the stateful façade Blazor components bind to. Owns `ConnectionProfile`, the browsed `TagTree`, `LastReadings`, busy/status flags, and fires a `StateChanged` event that pages subscribe to for re-rendering (this is a manual pub/sub, not Blazor's built-in state binding — pages call `Opc.StateChanged += StateHasChanged` in `OnInitialized` and unsubscribe in `Dispose`). Also owns cross-cutting live-data features layered on top of one subscription: CSV recording (wide-format, one column per subscribed tag) and the live trend chart's node selection. All public async operations are wrapped through the private `RunSafe` helper, which sets `IsBusy`/`StatusMessage`/`HasError` uniformly and swallows `OperationCanceledException` into a status message.
- **Services/DiagnosticsLogService.cs** — a bounded (500-entry) ring buffer of timestamped diagnostic strings, written to by `OpcUaClientService` during connect/browse/etc., displayed on the Diagnostics page.
- **Services/ThemeService.cs** — light/dark theme flag persisted to `%LocalAppData%\OpcUaExporter\app-settings.json`.
- **Models/OpcModels.cs** — all DTOs/enums in one file: `OpcTag` (tree node, `IsSelectable` = is a Variable), `TagReading`, `ConnectionProfile`, `NodeDetails`/`NodeAttributeInfo`/`NodeReferenceInfo` (node-properties panel), `ServerCapabilitiesInfo`/`ServerSecurityOption` (security discovery), `DiscoveredServerInfo` (port scan result), `ExportOptions`.

### UI structure (Components/)

- **Pages/Index.razor** — main tag-browser page: sidebar (connection form + certificate trust UI) + tag tree (`TagNode.razor`, recursive) + readings table + CSV/JSON export + recording controls + live trend chart. Largest file in the project; most feature wiring happens here.
- **Pages/ConnectionSettings.razor** — profile save/load, security mode/policy, authentication.
- **Pages/ServerDiscovery.razor** — host/port scanning UI (quick/full/custom scan) driven by `OpcUaService.QuickScanAsync`/`FullScanAsync`/`CustomScanAsync`.
- **Pages/Diagnostics.razor** — renders `DiagnosticsLogService.Entries`.
- **TagNode.razor** — recursive tree node component for the tag browser.
- **ThemeToggle.razor** — light/dark switch bound to `ThemeService`.
- **wwwroot/js/** — small JS interop helpers loaded by `index.html`: `resizable-panes.js` and `resizable-columns.js` (drag-resize for the layout), `trend-chart.js` (renders the live trend chart; fed via JSInterop from `Index.razor`, driven by `OpcUaService.TrendUpdate`).

### Key flows to know before changing subscription/recording/trend code

`OpcUaService` keeps exactly **one** active subscription (`_activeSubscription`) shared by live readings, CSV recording, and the trend chart. Selecting a different tag set for recording or trending will resubscribe (`SubscribeToAsync` tears down and recreates the subscription) if the requested set differs from `_subscribedNodeIds`. Recording and the trend chart both consume updates from the same `ApplySubscriptionUpdate` callback rather than having independent subscriptions — keep that invariant when touching this area.

## Project-specific conventions

- Modern, non-deprecated APIs only for OPC UA certificate handling (per `.github/copilot-instructions.md`) — when working in `OpcUaClientService.cs`'s certificate/security code, prefer current OPCFoundation SDK APIs over older/obsolete overloads.
- Services are DI singletons; new services that hold per-session state should follow the same pattern (register in `App.xaml.cs`, inject into Razor pages with `@inject`) rather than introducing scoped/transient lifetimes that wouldn't fit the single-window app model.
- All OPC UA client/PKI/log/settings state lives under `%LocalAppData%\OpcUaExporter\` (`pki\`, `crash.log`, `app-settings.json`, `last-profile.txt`) — keep new persisted state there too.
