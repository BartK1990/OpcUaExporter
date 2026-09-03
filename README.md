# OPC UA Exporter

A Windows desktop application built with **Blazor Hybrid (WPF)** that connects to OPC UA servers, browses their tag trees, reads/writes/subscribes to live values, and exports selected tags to CSV or JSON.

OPC UA communication is handled natively from .NET using the [OPC Foundation's UA-.NETStandard](https://github.com/OPCFoundation/UA-.NETStandard) client SDK — no external runtime or subprocess is involved.

<img width="1255" height="783" alt="opcuaexporter gif" src="https://github.com/user-attachments/assets/ce6c7b06-adcc-4552-89ce-715dc30faabf" />

---

## Architecture

```
┌──────────────────────────────────────────────────────────────┐
│  WPF Host (MainWindow.xaml)                                  │
│  ┌──────────────────────────────────────────────────────┐    │
│  │  BlazorWebView                                        │    │
│  │  ┌────────────────────────────────────────────────┐  │    │
│  │  │  Blazor Components (Index.razor, TagNode, ...)  │  │    │
│  │  │           ↕ DI                                  │  │    │
│  │  │  OpcUaService  ──►  OpcUaClientService          │  │    │
│  │  └────────────────────────────────────────────────┘  │    │
│  └──────────────────────────────────────────────────────┘    │
└───────────────────────────┬────────────────────────────────-─┘
                             │ Opc.Ua.Client (OPCFoundation SDK)
                             ▼
                      OPC UA Server (network)
```

### Key design decisions

| Layer | Technology | Why |
|---|---|---|
| UI host | WPF + BlazorWebView | Blazor Hybrid on Windows, native file dialogs, XAML layout |
| UI components | Razor + CSS | Modern reactive UI, no WinForms designer lock-in |
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
cd OpcUaExporter
dotnet build
dotnet run
```

Or open `OpcUaExporter.sln` in **Visual Studio 2022** and press **F5**.

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
OpcUaExporter/
├── OpcUaExporter.csproj
├── App.xaml                    ← WPF Application definition (no StartupUri)
├── App.xaml.cs                 ← DI container setup + OnStartup + global crash logging
├── MainWindow.xaml              ← WPF Window hosting BlazorWebView
├── MainWindow.xaml.cs           ← JS-invokable native dialogs (Microsoft.Win32)
│
├── Models/
│   └── OpcModels.cs             ← OpcTag, TagReading, ConnectionProfile, NodeDetails,
│                                   ServerCapabilitiesInfo, DiscoveredServerInfo, ExportOptions, ...
│
├── Services/
│   ├── OpcUaClientService.cs    ← native OPC UA client: sessions, browse, read/write,
│   │                               subscriptions, endpoint/security discovery, port scanning,
│   │                               certificate trust handling
│   ├── OpcUaService.cs          ← high-level state management for Blazor (façade over
│   │                               OpcUaClientService), recording, trend chart wiring
│   ├── DiagnosticsLogService.cs ← bounded in-memory diagnostic log
│   └── ThemeService.cs          ← light/dark theme, persisted to app-settings.json
│
├── Components/
│   ├── App.razor
│   ├── _Imports.razor
│   ├── TagNode.razor            ← recursive tag tree component
│   ├── ThemeToggle.razor
│   └── Pages/
│       ├── Index.razor          ← main page (sidebar + tag tree + readings + export)
│       ├── ConnectionSettings.razor
│       ├── ServerDiscovery.razor← host/port scanning UI
│       └── Diagnostics.razor
│
└── wwwroot/
    ├── index.html
    ├── app.ico
    ├── css/
    │   └── app.css
    └── js/
        ├── resizable-panes.js   ← drag-resize layout panes
        ├── resizable-columns.js ← drag-resize table columns
        └── trend-chart.js       ← live trend chart rendering
```

---

## Security & Certificates

`OpcUaClientService` maintains its own PKI store under `%LocalAppData%\OpcUaExporter\pki\` (`own`, `trusted`, `issuer`, `rejected` directories) and generates a client application certificate on first run. Untrusted server certificates are **not** auto-accepted or auto-rejected — they are surfaced in the UI so you can review and trust/reject them explicitly, after which the connection can be retried.

Supported authentication: Anonymous and Username/Password. Security mode/policy (None, Sign, SignAndEncrypt with the standard OPC UA security policies) can be selected per connection profile, or discovered from the server via **Connection Settings → Discover Modes**.

---

## Publishing

```bash
dotnet publish -c Release -r win-x64 --self-contained true
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
