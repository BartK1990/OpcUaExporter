# OPC UA Exporter

A Windows desktop application built with **Blazor Hybrid (WPF)** that connects to OPC UA servers, browses their tag trees, reads/writes/subscribes to live values, and exports selected tags to CSV, JSON, or Excel (.xlsx).

OPC UA communication is handled natively from .NET using the [OPC Foundation's UA-.NETStandard](https://github.com/OPCFoundation/UA-.NETStandard) client SDK — no external runtime or subprocess is involved.

<img width="1401" height="1007" alt="GIF 2026-09-06 23-17-43" src="https://github.com/user-attachments/assets/bb262715-6b95-40f5-884a-1788d885bd56" />

---

## Installing a release package

You don't need the .NET SDK or Visual Studio to use the app — download a
ready-built package from the
[latest release](https://github.com/BartK1990/OpcUaExporter/releases/latest).
Each release ships three ZIP files:

| File | What it is | Needs installed on the PC |
|---|---|---|
| `OpcUaExporter-<version>-self-contained.zip` | 64-bit build with the .NET runtime bundled — **recommended** | nothing extra |
| `OpcUaExporter-<version>-win_x64.zip` | 64-bit build, smaller, uses the shared .NET runtime | [.NET 8 Desktop Runtime (x64)](https://dotnet.microsoft.com/download/dotnet/8.0) |
| `OpcUaExporter-<version>-portable.zip` | Build not tied to one CPU architecture, uses the shared .NET runtime | [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) |

All variants also need the **Microsoft Edge WebView2 Runtime**, which is
preinstalled on Windows 11 and on up-to-date Windows 10. If the window stays
blank, install it from
[Microsoft](https://developer.microsoft.com/microsoft-edge/webview2/).

There is no installer:

1. Download the `.zip` you want (the `self-contained` one if unsure).
2. Extract it to a folder of your choice, e.g. `C:\Tools\OpcUaExporter\`.
   Extract the whole archive — don't run the app from inside the zip.
3. Start `OpcUaExporter.exe` from that folder.

The executable isn't code-signed, so on first launch Windows SmartScreen may
show *"Windows protected your PC"* — click **More info → Run anyway**.

To update, extract the new version over the old folder (or into a new one).
To uninstall, delete the folder. Settings, saved profiles and certificates live
in `%LocalAppData%\OpcUaExporter\`, so they survive updates; delete that folder
too if you want to remove everything.

The rest of this README covers how the app is built and how to build it from
source.

---

## Architecture

The application is split into a UI library, a core library and two
interchangeable hosts, with a one-way dependency chain:

```
┌────────────────────────────────┐  ┌────────────────────────────────┐
│  OpcUaExporter.Wpf             │  │  OpcUaExporter.Web             │
│  (net8.0-windows, WinExe)      │  │  (net8.0, ASP.NET Core)        │
│  Shipped desktop app: WPF      │  │  Dev/test host: the same UI    │
│  window + BlazorWebView,       │  │  in any browser on any OS      │
│  native dialogs, crash log     │  │  (Blazor interactive server)   │
└────────────────┬───────────────┘  └────────────────┬───────────────┘
                 └───────────┬───────────────────────┘
                             │ references
┌────────────────────────────▼────────────────────────────────────┐
│  OpcUaExporter.UI           (net8.0, Razor class library)       │
│  Blazor pages, components and static web assets                 │
│  (served from _content/OpcUaExporter.UI/)                       │
└────────────────────────────┬────────────────────────────────────┘
                             │ references
┌────────────────────────────▼────────────────────────────────────┐
│  OpcUaExporter.Core         (net8.0, class library)             │
│  Models, OpcUaService (state) ──► OpcUaClientService (session)  │
│  plus the platform abstractions the UI depends on               │
└────────────────────────────┬────────────────────────────────────┘
                             │ Opc.Ua.Client (OPCFoundation SDK)
                             ▼
                  OPC UA Server (network)
```

Neither `OpcUaExporter.Core` nor `OpcUaExporter.UI` references WPF or any
Windows-only API, so both build and can be unit tested on any OS. Anything the
UI needs from the host — currently only native file dialogs and message boxes —
is expressed as an abstraction in Core (`IFileDialogService`) and implemented by
each host (`WpfFileDialogService`, `BrowserFileDialogService`). That inverts what used to be a hard
dependency on static `[JSInvokable]` methods in the WPF window.

### Key design decisions

| Layer | Technology | Why |
|---|---|---|
| UI host | WPF + BlazorWebView (`OpcUaExporter.Wpf`) | Blazor Hybrid on Windows, native file dialogs, XAML layout |
| Browser host | ASP.NET Core, Blazor interactive server (`OpcUaExporter.Web`) | Runs the identical UI in a browser on any OS, so it can be developed and end-to-end tested without Windows |
| UI components | Razor class library (`OpcUaExporter.UI`) | Modern reactive UI, testable in isolation, no Windows dependency |
| Domain + services | Class library (`OpcUaExporter.Core`) | OPC UA and application state without a UI framework attached |
| Platform services | `IFileDialogService` in Core, implemented in the shell | Keeps the UI portable and lets tests substitute a stub |
| OPC UA client | OPCFoundation.NetStandard.Opc.Ua(.Client/.Configuration) | Official, actively maintained .NET OPC UA SDK — no subprocess, no external runtime to bundle |
| State | `OpcUaService` singleton | Central façade Blazor components bind to; raises a `StateChanged` event for re-render |

`OpcUaClientService` owns the OPC UA session lifecycle directly: building the client `ApplicationConfiguration` (including its own application certificate), endpoint discovery and selection by security mode/policy, browsing, reading, writing, and subscriptions — all via `Opc.Ua.Client` types (`Session`, `Subscription`, `MonitoredItem`, `DiscoveryClient`).

---

## Prerequisites

- **Windows 10/11** for the desktop app (the browser host, simulation server and tests run on any OS)
- **.NET 8 SDK** (https://dotnet.microsoft.com/download)

No other setup is required — the OPC UA client library is a standard NuGet dependency restored on build.

---

## Build & Run

```bash
dotnet build OpcUaExporter.sln
dotnet run --project src/OpcUaExporter.Wpf
```

Or open `OpcUaExporter.sln` in **Visual Studio 2022** and press **F5**.

(`OpcUaExporter.Wpf` targets `net8.0-windows`, so on Linux/macOS build the
individual projects below instead of the whole solution.)

### Run in a browser (any OS)

The same UI can be hosted in a browser — handy for developing the UI on
Linux/macOS and for testing:

```bash
dotnet run --project src/OpcUaExporter.Web     # → http://localhost:5080
```

It is a local, single-user development host, not a deployment target: all tabs
share one connection (like the desktop window), and the save/open "dialogs" ask
for a path on the machine running the host. Set `OPCUAEXPORTER_DATA_DIR` to keep
its profiles, settings and certificates apart from the desktop app's
`%LocalAppData%\OpcUaExporter\`.

### Simulation server

No OPC UA server at hand? Start the bundled simulation server and connect to
`opc.tcp://localhost:4840`:

```bash
dotnet run --project tools/OpcUaExporter.SimServer [-- --port 4840]
```

It serves `Objects/Simulation` with values that change every second
(`Dynamic/…`), writable tags of the common built-in types including `DateTime`
(`Static/…`), and a nested branch (`Deep/…`), over both `None` and
`Basic256Sha256` security.

### Tests

```bash
dotnet test tests/OpcUaExporter.Tests       # unit tests
dotnet test tests/OpcUaExporter.E2ETests    # end-to-end UI tests
```

Both run on Windows, Linux and macOS. The unit tests cover Core and the UI's
services. The end-to-end tests start the simulation server and the browser host
in-process and drive the real UI with Playwright in headless Chromium —
browsing, selection, writes (including DateTime), subscriptions and the trend
chart. They need a Playwright Chromium build: point `PLAYWRIGHT_BROWSERS_PATH`
at one, or install it once after building:

```bash
pwsh tests/OpcUaExporter.E2ETests/bin/Debug/net8.0/playwright.ps1 install chromium
```

Only the WPF shell itself (`OpcUaExporter.Wpf`) is not covered — keep it thin.

---

## Usage

1. **Enter the server endpoint URL** (e.g. `opc.tcp://192.168.1.10:4840`) on the Tag Browser sidebar, or use **Discover Servers** to scan a host's ports for OPC UA endpoints.
2. Configure security mode/policy and authentication (anonymous or username/password) under **Connection Settings**, and click **Save** to add the profile to your saved-profiles library (a **Profile** dropdown appears once you have one, so you can switch between multiple servers — production, test, different machines/lines — without re-entering settings each time). **New**/**Duplicate**/**Delete** manage the library, and **Export…**/**Import…** move a single profile to/from an arbitrary file.
3. Click **Browse Tags** — the address space tree loads in the tag browser (top-level structure appears first, then the tree fills in as the deep scan continues in the background; scanning can run in parallel — see `ConnectionProfile.EnableParallelBrowse`). You don't have to wait for it: expanding a node the scan hasn't reached yet fetches its children from the server right away.
4. **Check** the tags you want (or use All / None / per-folder shortcuts).
5. Click **Read Values** for a one-off read, or **Subscribe** to get live updates pushed into the readings table. Subscribe/trend checkboxes respond instantly — changes are queued and applied to the server in the background (a small spinner marks rows still being applied).
6. With a live subscription active, optionally **record** to a CSV file (one row per interval — 1 s by default, adjustable next to the button — stamped with whole-second UTC timestamps) and/or plot selected tags on the **live trend chart** (the tag list shows each tag's current value; hover the chart for a crosshair with the values at that second).
7. Choose a **format** (CSV, JSON, or Excel), enter or browse for an **output path**, and click **Export Selected**.
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
│   ├── OpcUaExporter.Web/           ← net8.0 · browser host for development and E2E tests
│   │   ├── Program.cs / OpcUaExporterWebApp.cs  ← builds the app (also used by the E2E tests)
│   │   ├── Components/App.razor     ← host page (counterpart of the WPF index.html)
│   │   └── Services/
│   │       └── BrowserFileDialogService.cs  ← IFileDialogService via prompt/confirm/alert
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
├── tools/
│   └── OpcUaExporter.SimServer/     ← net8.0 · OPC UA simulation server (not shipped)
│
└── tests/
    ├── OpcUaExporter.Tests/         ← net8.0 · xUnit unit tests
    └── OpcUaExporter.E2ETests/      ← net8.0 · xUnit + Playwright end-to-end UI tests
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
