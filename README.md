# OPC UA Exporter

A Windows desktop application built with **Blazor Hybrid (WPF)** that connects to OPC UA servers, browses their tag trees, reads/writes/subscribes to live values, and exports selected tags to CSV or JSON.

OPC UA communication is handled natively from .NET using the [OPC Foundation's UA-.NETStandard](https://github.com/OPCFoundation/UA-.NETStandard) client SDK — no external runtime or subprocess is involved.

<img width="1401" height="1007" alt="GIF 2026-09-06 23-17-43" src="https://github.com/user-attachments/assets/bb262715-6b95-40f5-884a-1788d885bd56" />

---

## Architecture

The application is split into three projects, each with a single
responsibility and a one-way dependency chain:

```
┌───────────────────────────────────────────────────────────────┐
│  OpcUaExporter.Wpf          (net8.0-windows, WinExe)          │
│  WPF shell: process lifetime, DI container, crash logging,     │
│  BlazorWebView host, native dialogs (IFileDialogService)       │
└───────────────────────────┬───────────────────────────────────┘
                            │ references
┌───────────────────────────▼───────────────────────────────────┐
│  OpcUaExporter.UI           (net8.0, Razor class library)      │
│  Blazor pages, components and static web assets                │
│  (served from _content/OpcUaExporter.UI/)                      │
└───────────────────────────┬───────────────────────────────────┘
                            │ references
┌───────────────────────────▼───────────────────────────────────┐
│  OpcUaExporter.Core         (net8.0, class library)            │
│  Models, OpcUaService (state) ──► OpcUaClientService (session) │
│  plus the platform abstractions the UI depends on              │
└───────────────────────────┬───────────────────────────────────┘
                            │ Opc.Ua.Client (OPCFoundation SDK)
                            ▼
                     OPC UA Server (network)
```

Neither `OpcUaExporter.Core` nor `OpcUaExporter.UI` references WPF or any
Windows-only API, so both build and can be unit tested on any OS. Anything the
UI needs from the host — currently only native file dialogs and message boxes —
is expressed as an abstraction in Core (`IFileDialogService`) and implemented by
the shell (`WpfFileDialogService`). That inverts what used to be a hard
dependency on static `[JSInvokable]` methods in the WPF window.

### Key design decisions

| Layer | Technology | Why |
|---|---|---|
| UI host | WPF + BlazorWebView (`OpcUaExporter.Wpf`) | Blazor Hybrid on Windows, native file dialogs, XAML layout |
| UI components | Razor class library (`OpcUaExporter.UI`) | Modern reactive UI, testable in isolation, no Windows dependency |
| Domain + services | Class library (`OpcUaExporter.Core`) | OPC UA and application state without a UI framework attached |
| Platform services | `IFileDialogService` in Core, implemented in the shell | Keeps the UI portable and lets tests substitute a stub |
| OPC UA client | OPCFoundation.NetStandard.Opc.Ua(.Client/.Configuration) | Official, actively maintained .NET OPC UA SDK — no subprocess, no external runtime to bundle |
| State | `OpcUaService` singleton | Central façade Blazor components bind to; raises a `StateChanged` event for re-render |

`OpcUaClientService` owns the OPC UA session lifecycle directly: building the client `ApplicationConfiguration` (including its own application certificate), endpoint discovery and selection by security mode/policy, browsing, reading, writing, and subscriptions — all via `Opc.Ua.Client` types (`Session`, `Subscription`, `MonitoredItem`, `DiscoveryClient`).

---

## Prerequisites

- **Windows 10/11**
- **.NET 8 SDK** (https://dotnet.microsoft.com/download)

No other setup is required — the OPC UA client library is a standard NuGet dependency restored on build.

---

## Build & Run

```bash
dotnet build OpcUaExporter.sln
dotnet run --project src/OpcUaExporter.Wpf
```

Or open `OpcUaExporter.sln` in **Visual Studio 2022** and press **F5**.

### Tests

```bash
dotnet test tests/OpcUaExporter.Tests
```

The test project targets `net8.0` and references only `OpcUaExporter.Core` and
`OpcUaExporter.UI`, so it runs on Windows, Linux and macOS alike.

---

## Usage

1. **Enter the server endpoint URL** (e.g. `opc.tcp://192.168.1.10:4840`) on the Tag Browser sidebar, or use **Discover Servers** to scan a host's ports for OPC UA endpoints.
2. Configure security mode/policy and authentication (anonymous or username/password) under **Connection Settings**, and save the profile for reuse.
3. Click **Browse Tags** — the address space tree loads in the tag browser (top-level structure appears first, then the tree fills in as the deep scan continues; scanning can run in parallel — see `ConnectionProfile.EnableParallelBrowse`).
4. **Check** the tags you want (or use All / None / per-folder shortcuts).
5. Click **Read Values** for a one-off read, or **Subscribe** to get live updates pushed into the readings table.
6. With a live subscription active, optionally **record** updates to a CSV file and/or plot selected tags on the **live trend chart**.
7. Choose a **format** (CSV or JSON), enter or browse for an **output path**, and click **Export Selected**.
8. If the server presents an untrusted certificate, it appears in the sidebar for you to **Trust** or **Reject** before retrying the connection.
9. Check the **Diagnostics** page for a running log of connection/browse/read/subscribe activity.

---

## Project Structure

```
OpcUaExporter.sln
Directory.Build.props                ← properties shared by every project
│
├── src/
│   ├── OpcUaExporter.Core/          ← net8.0 · no UI dependency
│   │   ├── AppPaths.cs              ← every %LocalAppData%\OpcUaExporter\ path
│   │   ├── ServiceCollectionExtensions.cs  ← AddOpcUaExporterCore()
│   │   ├── Abstractions/
│   │   │   ├── IFileDialogService.cs      ← native dialogs the host supplies
│   │   │   └── NullFileDialogService.cs   ← default: every prompt cancels
│   │   ├── Models/
│   │   │   └── OpcModels.cs         ← OpcTag, TagReading, ConnectionProfile, NodeDetails,
│   │   │                               ServerCapabilitiesInfo, DiscoveredServerInfo, ExportOptions, ...
│   │   └── Services/
│   │       ├── OpcUaClientService.cs    ← native OPC UA client: sessions, browse, read/write,
│   │       │                               subscriptions, endpoint/security discovery, port
│   │       │                               scanning, certificate trust handling
│   │       ├── OpcUaService.cs          ← high-level state management for Blazor (façade over
│   │       │                               OpcUaClientService), recording, trend chart wiring
│   │       ├── DiagnosticsLogService.cs ← bounded in-memory diagnostic log
│   │       └── ThemeService.cs          ← light/dark theme, persisted to app-settings.json
│   │
│   ├── OpcUaExporter.UI/            ← net8.0 · Razor class library
│   │   ├── ServiceCollectionExtensions.cs  ← AddOpcUaExporterUi()
│   │   ├── Components/
│   │   │   ├── Routes.razor         ← Blazor router (root component)
│   │   │   ├── _Imports.razor
│   │   │   ├── AppSidebar.razor
│   │   │   ├── TagNode.razor        ← recursive tag tree component
│   │   │   ├── ThemeToggle.razor
│   │   │   └── Pages/
│   │   │       ├── Index.razor      ← main page (sidebar + tag tree + readings + export)
│   │   │       ├── ConnectionSettings.razor
│   │   │       ├── ServerDiscovery.razor  ← host/port scanning UI
│   │   │       └── Diagnostics.razor
│   │   └── wwwroot/                 ← published as _content/OpcUaExporter.UI/
│   │       ├── css/app.css
│   │       └── js/
│   │           ├── resizable-panes.js   ← drag-resize layout panes
│   │           ├── resizable-columns.js ← drag-resize table columns
│   │           ├── trend-chart.js       ← live trend chart rendering
│   │           └── theme.js             ← applies data-theme to the document
│   │
│   └── OpcUaExporter.Wpf/           ← net8.0-windows · WinExe (OpcUaExporter.exe)
│       ├── App.xaml / App.xaml.cs   ← DI container, OnStartup, global crash logging
│       ├── MainWindow.xaml / .cs    ← WPF Window hosting BlazorWebView
│       ├── Services/
│       │   └── WpfFileDialogService.cs  ← IFileDialogService via Microsoft.Win32 dialogs
│       └── wwwroot/
│           ├── index.html           ← host page
│           └── app.ico
│
└── tests/
    └── OpcUaExporter.Tests/         ← net8.0 · xUnit
```

---

## Security & Certificates

`OpcUaClientService` maintains its own PKI store under `%LocalAppData%\OpcUaExporter\pki\` (`own`, `trusted`, `issuer`, `rejected` directories) and generates a client application certificate on first run. Untrusted server certificates are **not** auto-accepted or auto-rejected — they are surfaced in the UI so you can review and trust/reject them explicitly, after which the connection can be retried.

Supported authentication: Anonymous and Username/Password. Security mode/policy (None, Sign, SignAndEncrypt with the standard OPC UA security policies) can be selected per connection profile, or discovered from the server via **Connection Settings → Discover Modes**.

---

## Publishing

```bash
dotnet publish src/OpcUaExporter.Wpf -c Release -r win-x64 --self-contained true
```

The published output is self-contained — there is no separate runtime folder to copy alongside it.

---

## Troubleshooting

| Symptom | Fix |
|---|---|
| Blank WebView / `blazor.webview.js` 404 | Ensure `Microsoft.AspNetCore.Components.WebView.Wpf` NuGet is installed |
| Connection timeout | Check firewall, confirm OPC UA server is running and the port is open |
| `No endpoint matches SecurityMode=... and SecurityPolicy=...` | Use **Discover Modes** on the Connection Settings page to see the security options the server actually offers, then match your profile to one of them |
| Certificate errors on connect | Check the **Server Certificates** panel on the Tag Browser sidebar for a pending certificate to trust/reject |
| `Client application certificate key size is ...` on startup | Delete `%LocalAppData%\OpcUaExporter\pki\own` and restart the app to regenerate a stronger certificate |
