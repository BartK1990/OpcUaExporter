# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

This repository holds **two applications** that share one OPC UA client library.

**OPC UA Exporter** is a Windows desktop app (WPF host + Blazor Hybrid UI) that connects to OPC UA servers, browses their address space, reads/writes/subscribes to live tag values, and exports selected tags to CSV, JSON or Excel.

**OPC UA Bridge** is a headless client-to-server gateway, installable as a Windows service. It holds one supervised, auto-reconnecting session to an unreliable upstream OPC UA server and mirrors that server's address space on its own `opc.tcp` endpoint, so an application that kept losing its connection can point at the bridge instead and stop noticing the outages.

OPC UA communication uses the native **OPCFoundation UA-.NETStandard** SDK directly from C# — there is no Python subprocess or embedded runtime. [README.md](README.md) is kept in sync with this architecture.

The solution has seven projects (`src/` + `tests/`), described under "Architecture" below.

## Commands

**On Linux (including cloud/CI sessions), use the solution filter.** `OpcUaExporter.Wpf` is `net8.0-windows` and can never build there, so `dotnet build OpcUaExporter.sln` will always fail on that project. That failure is expected and unrelated to code correctness — don't try to "fix" it.

```bash
dotnet build OpcUaExporter.Linux.slnf          # everything except the WPF shell
dotnet test tests/OpcUaExporter.Tests
dotnet test tests/OpcUaBridge.Tests --filter "Category!=Integration"
```

Exporter (Windows only):
```bash
dotnet build OpcUaExporter.sln
dotnet run --project src/OpcUaExporter.Wpf
dotnet publish src/OpcUaExporter.Wpf -c Release -r win-x64 --self-contained true
```

Bridge (runs anywhere):
```bash
dotnet run --project src/OpcUaBridge.Host                  # dashboard on http://127.0.0.1:5080
dotnet run --project src/OpcUaBridge.Host -- --check-config
dotnet publish src/OpcUaBridge.Host -c Release -r win-x64 --self-contained true
```

### What the tests do and don't cover

The exporter's tests cover only platform-independent pieces (models, the diagnostics buffer, path layout, DI wiring). Anything touching a live OPC UA session or the WebView still has to be verified by running the app.

The bridge's tests go further. Its **integration tests start a real OPC UA server in-process and connect a real client to it**, and they run on Linux — the OPC UA SDK is cross-platform. `EndToEndGatewayTests` stands up a second server as a stand-in plant server and drives browse → capture → resolve → subscribe → mirror against it. They are excluded from the default run because they are slow and generate certificates:

```bash
dotnet test tests/OpcUaBridge.Tests --filter "Category=Integration"
```

Prefer extending those over adding a fake when changing anything in the gateway's data path. They are what catches the joins between pieces that unit tests each pass in isolation.

## Architecture

```
  Desktop exporter                        Headless gateway
  ────────────────                        ────────────────
src/OpcUaExporter.Wpf                   src/OpcUaBridge.Host
  (net8.0-windows, WinExe                 (net8.0, Web SDK,
   → OpcUaExporter.exe)                    → OpcUaBridge.exe)
      │ references                              │ references
src/OpcUaExporter.UI                    src/OpcUaBridge.Core
  (net8.0, Razor class library)           (net8.0, class library)
      │ references                              │
src/OpcUaExporter.Core                          │
  (net8.0, class library)                       │
      │                                         │
      └──────────────┬──────────────────────────┘
                     ▼
              src/OpcUaShared
         (net8.0, OPC UA client primitives)
                     │
   OPCFoundation UA-.NETStandard → OPC UA server (network)
```

Two rules keep this honest, and both are enforced by tests:

- **`OpcUaExporter.Core` and `.UI` must never take a WPF or Windows-only dependency**, which is what keeps them buildable and testable off Windows. When the UI needs something only the host can do, declare an abstraction in `Core/Abstractions/` and implement it in the WPF project.
- **`OpcUaBridge.*` must never reference `OpcUaExporter.Core` or `.UI`.** They share the OPC UA client code, not the desktop application; letting the exporter in would drag CsvHelper, an Excel writer and a `%LocalAppData%` path layout into a Windows service that wants none of them. `ArchitectureTests` in `tests/OpcUaBridge.Tests` asserts this, along with `OpcUaBridge.Core` taking no ASP.NET Core dependency.

### OpcUaShared — the OPC UA client, shared by both applications

`RootNamespace` is `OpcUaExporter`, so the types here keep the namespaces they had before the split (`OpcUaExporter.Models`, `.Services`, …). That is deliberate: it let the extraction happen without touching a single `.razor` file, test or `using` elsewhere. Keep new types in namespaces that match.

The operation primitives all **take a session the caller owns** rather than creating one. That is the whole point of the split: the exporter opens a session per operation, while the gateway drives the same code against one long-lived, supervised session.

- **Configuration/OpcUaApplicationConfigurationFactory.cs** — builds and validates the client `ApplicationConfiguration` and ensures the app certificate. Takes an `OpcUaApplicationIdentity` and an `IOpcUaPkiLocation`, which are the only two things that genuinely vary between the two applications.
- **Abstractions/IOpcUaPkiLocation.cs** — where the certificate stores live. `%LocalAppData%\OpcUaExporter\pki` for the desktop app, `<exe>\pki\client` for the service.
- **Certificates/CertificateTrustStore.cs** — pending/trusted server certificates. Untrusted ones are captured for a human to decide on, never auto-accepted or silently dropped.
- **Sessions/OpcUaSessionFactory.cs** — endpoint selection and session creation. (Namespace is `Sessions`, plural, because the singular collides with `Opc.Ua.Client.Session` at every use site.)
- **Operations/** — `OpcUaBrowser`, `OpcUaValueReader`, `OpcUaValueWriter`, `OpcUaNodeInspector`, `OpcUaSubscriber`, `OpcUaDataTypes`. Note `OpcUaValueReader` has two read paths: `ReadDescribedAsync` costs two round trips per node to fetch display names and data types (right for a table a human is reading) and `ReadValuesAsync`/`ReadAttributeAsync` batch to the server's `MaxNodesPerRead` (the only viable path at a few thousand tags).
- **Discovery/OpcUaServerScanner.cs** — port scanning and endpoint capability discovery.
- **Services/OpcUaClientService.cs** — a façade over the above that opens and closes a session per call, preserving the exact public surface the exporter has always used.

### OpcUaExporter.Core — models and services

- **AppPaths.cs** — the only place that knows about `%LocalAppData%\OpcUaExporter\`. Everything persisted (`pki\`, `crash.log`, `app-settings.json`, `last-profile.txt`) is addressed through it; add new persisted state here rather than recomputing the directory.
- **ServiceCollectionExtensions.cs** — `AddOpcUaExporterCore()`, the single registration point for the services below. All singletons so Blazor components share state; uses `TryAdd*` so a host can pre-register its own implementation of an abstraction.
- **Abstractions/IFileDialogService.cs** — native save/open/confirm/message dialogs. Blazor Hybrid has no browser file picker, so the shell supplies these. `NullFileDialogService` is the default and cancels every prompt (component tests, previews).
- **`OpcUaClientService` now lives in `OpcUaShared`** (see above), not here, though its namespace and public surface are unchanged.
- **Services/OpcUaService.cs** — the stateful façade Blazor components bind to. Owns `ConnectionProfile`, the browsed `TagTree`, `LastReadings`, busy/status flags, and fires a `StateChanged` event that pages subscribe to for re-rendering (this is a manual pub/sub, not Blazor's built-in state binding — pages call `Opc.StateChanged += StateHasChanged` in `OnInitialized` and unsubscribe in `Dispose`). Also owns cross-cutting live-data features layered on top of one subscription: CSV recording (wide-format, one column per subscribed tag) and the live trend chart's node selection. All public async operations are wrapped through the private `RunSafe` helper, which sets `IsBusy`/`StatusMessage`/`HasError` uniformly and swallows `OperationCanceledException` into a status message.
- **`DiagnosticsLogService`** — a bounded (500-entry) ring buffer of timestamped diagnostic strings, written to by the OPC UA client during connect/browse/etc. and displayed on both applications' Diagnostics pages. Lives in `OpcUaShared/Services/`.
- **Services/ThemeService.cs** — light/dark theme flag persisted to `AppPaths.SettingsFile`.
- **Models/ExportModels.cs** — `ExportOptions` and `ExportFormat`, which are the exporter's alone. The OPC UA domain models (`OpcTag`, `TagReading`, `ConnectionProfile`, `NodeDetails`, …) live in `OpcUaShared/Models/OpcModels.cs`, still in namespace `OpcUaExporter.Models`.

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

### OpcUaBridge.Core — the gateway

Namespace `OpcUaBridge`. No hosting or UI dependency, so all of it is testable on any OS.

- **BridgePaths.cs** — every file the bridge touches, resolved from `AppContext.BaseDirectory`. A Windows service starts with its working directory set to `C:\Windows\System32`, so **nothing in the bridge may compose a path from `Directory.GetCurrentDirectory()`**. `Logs\`, `config\` and `pki\` all sit beside the executable, which is also what makes the install one portable folder.
- **Configuration/** — `BridgeOptions` and `BridgeOptionsValidator` (fails startup on configurations that would run but misbehave), plus `AddOpcUaBridge()`, the single registration point.
- **Upstream/UpstreamConnectionManager.cs** — the supervised session. Keep-alive monitoring, `SessionReconnectHandler`, a watchdog for keep-alives that simply stop arriving, and exponential backoff with jitter. Splits failures into transient (retry forever) and unrecoverable (`Faulted`, wait for an operator) — retrying a rejected certificate every second buries the real problem and loads a server that has already said no.
- **Namespaces/** — the captured address space. `INamespaceSnapshotStore` is read-only and is what the entire runtime pipeline is injected; `INamespaceSnapshotWriter` is held **only** by `NamespaceCaptureService`, which is reachable only from an explicit operator action. That type split is how "the saved namespace never changes without an explicit command" is enforced structurally rather than by convention — keep it that way.
- **Tags/** — `TagRegistry` (dense array of `MirrorTag`, indexed by a contiguous `int`) and `TagValueStore` (the hot path: allocation-free publishing with coalescing dirty flags).
- **Acquisition/** — `SubscriptionAcquisitionEngine` and `PollingAcquisitionEngine` behind `IAcquisitionEngine`, with `AcquisitionCoordinator` starting and rebuilding them as the connection comes and goes.
- **Server/** — `BridgeServer` (`StandardServer`), `MirrorNodeManager` (`CustomNodeManager2`), `MirrorValueApplier` (batched value application) and `UpstreamWriteRouter` (write pass-through).
- **Certificates/ExporterCertificateImporter.cs** — copies `%LocalAppData%\OpcUaExporter\pki` into the bridge's client store, so an operator migrating from the desktop app does not have to re-trust anything on the plant server.

### OpcUaBridge.Host — the service

- **Program.cs** — sets the working directory to `AppContext.BaseDirectory` as its **first statement**, then builds the host with an explicit `ContentRootPath`. Both are needed; see the comment there.
- **BridgeLogging.cs** — Serilog, configured in code so the 30 MB / 10 file limits cannot be lost to a careless `appsettings.json` edit. The file sink is asynchronous and non-blocking because a disk stall must never hold up the SDK's publish thread.
- **BridgeCommands.cs** — `--check-config`, `--check-pki`, `--import-exporter-certificates`, `--show-namespace`.
- **Components/Pages/** — Dashboard, Tags (virtualized), Namespace (capture with diff and confirm), Diagnostics.

### Key flows to know before changing gateway code

**Namespace indexes are session-scoped.** A snapshot records namespace **URIs**, and `TagRegistry.ResolveAgainst` rebuilds every upstream NodeId from its URI on each new session. Never persist or reuse a bare `ns=N;…` across sessions: servers reorder their namespace array across restarts, and the failure mode is the mirror serving completely different tags while reporting them `Good`.

**The value path must not allocate or block.** `TagValueStore.Publish` runs on the SDK's publish thread for every value of every tag. A test asserts it allocates nothing. Values reach the mirror through `MirrorValueApplier`, which batches them under a single node-manager lock — per-value locking would contend with every downstream browse and read.

**Writes are never queued.** A write held during an outage and replayed on reconnect would apply a setpoint minutes late to a process that has moved on. `UpstreamWriteRouter` fails immediately with the upstream's own status code.

## Project-specific conventions

- Modern, non-deprecated APIs only for OPC UA certificate handling (per `.github/copilot-instructions.md`) — when working in `OpcUaShared`'s certificate/security code, prefer current OPCFoundation SDK APIs over older/obsolete overloads.
- Services are DI singletons; new services that hold per-session state should follow the same pattern (register in `AddOpcUaExporterCore()`, inject into Razor pages with `@inject`) rather than introducing scoped/transient lifetimes that wouldn't fit the single-window app model. Only genuinely Windows-specific services are registered in `App.xaml.cs`.
- **The exporter's** OPC UA client/PKI/log/settings state lives under `%LocalAppData%\OpcUaExporter\` — address it through `AppPaths`, never by recomputing the directory. The bridge keeps its own beside its executable (see below).
- Anything the UI needs from the host goes through an abstraction in `OpcUaExporter.Core/Abstractions/`, implemented in `OpcUaExporter.Wpf/Services/`. Do not reintroduce static `[JSInvokable]` bridges: they hard-code the host assembly name and cannot be substituted in tests.
- The UI library's static assets are addressed as `_content/OpcUaExporter.UI/...` from the host page. Renaming the UI assembly changes that path.
- **Shared code goes in `OpcUaShared`, and it takes the session as a parameter.** Anything that opens its own session cannot be reused by the gateway, which has exactly one and must keep it. If the exporter needs create-use-dispose semantics, that belongs in the `OpcUaClientService` façade, not in the primitive.
- **The bridge keeps its state beside its executable**, addressed through `BridgePaths` — never `Directory.GetCurrentDirectory()`, which is the system directory when running as a service.
- The bridge holds **two OPC UA identities**: a client one the upstream server trusts (`<exe>\pki\client`) and a server one downstream clients trust (`<exe>\pki\server`). They are deliberately separate key pairs; don't merge them.
- `Bridge:Upstream:ClientApplicationName` defaults to **`OpcUaExporter`**, not `OpcUaBridge`. The SDK finds the application certificate by subject name, so this is what lets an operator copy their working exporter certificate store across and connect without touching the plant server. Changing it means issuing a new certificate and trusting it upstream.
