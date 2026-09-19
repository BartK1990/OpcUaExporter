# OPC UA Exporter & OPC UA Bridge

Two applications sharing one OPC UA client library.

**OPC UA Exporter** is a Windows desktop application built with **Blazor Hybrid (WPF)** that connects to OPC UA servers, browses their tag trees, reads/writes/subscribes to live values, and exports selected tags to CSV, JSON, or Excel (.xlsx).

**[OPC UA Bridge](#opc-ua-bridge)** is a headless client-to-server gateway that installs as a Windows service. It holds one supervised, automatically reconnecting session to an unreliable OPC UA server and republishes that server's address space on its own `opc.tcp` endpoint. An application that keeps losing its connection points at the bridge instead and stops noticing the outages — during one, the bridge keeps serving the last known values with `UncertainLastUsableValue` rather than dropping the session.

OPC UA communication is handled natively from .NET using the [OPC Foundation's UA-.NETStandard](https://github.com/OPCFoundation/UA-.NETStandard) client and server SDKs — no external runtime or subprocess is involved.

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

Two applications, each a one-way dependency chain, meeting at a shared OPC UA
client library:

```
      Desktop exporter                          Headless gateway
      ────────────────                          ────────────────
┌───────────────────────────────┐   ┌──────────────────────────────────┐
│ OpcUaExporter.Wpf             │   │ OpcUaBridge.Host                 │
│ (net8.0-windows, WinExe)      │   │ (net8.0, → OpcUaBridge.exe)      │
│ WPF shell, DI, crash logging, │   │ Windows service, Serilog,        │
│ BlazorWebView, file dialogs   │   │ Blazor Server dashboard          │
└──────────────┬────────────────┘   └───────────────┬──────────────────┘
               │ references                         │ references
┌──────────────▼────────────────┐   ┌───────────────▼──────────────────┐
│ OpcUaExporter.UI              │   │ OpcUaBridge.Core                 │
│ (net8.0, Razor class library) │   │ (net8.0, class library)          │
│ Blazor pages and web assets   │   │ Supervised upstream connection,  │
└──────────────┬────────────────┘   │ acquisition engines, namespace   │
               │ references         │ snapshot, mirrored OPC UA server │
┌──────────────▼────────────────┐   └───────────────┬──────────────────┘
│ OpcUaExporter.Core            │                   │
│ (net8.0, class library)       │                   │
│ OpcUaService (UI state),      │                   │
│ CSV/JSON/Excel export,        │                   │
│ saved profiles, theming       │                   │
└──────────────┬────────────────┘                   │
               │                                    │
               └───────────────┬────────────────────┘
                               ▼
                    ┌──────────────────────────┐
                    │ OpcUaShared (net8.0)     │
                    │ Application config/PKI,  │
                    │ endpoint selection,      │
                    │ sessions, browse, read,  │
                    │ write, subscribe         │
                    └────────────┬─────────────┘
                                 │ OPCFoundation UA-.NETStandard
                                 ▼
                        OPC UA server (network)
```

`OpcUaShared`'s operation primitives all take a session the caller owns rather
than creating one. That is what lets the same browse, read and write code serve
both a desktop tool that opens a session per operation and a gateway that holds
exactly one open for months.

`OpcUaBridge.*` deliberately does **not** reference `OpcUaExporter.Core`: they
share the OPC UA client, not the desktop application, and a Windows service has
no business carrying an Excel writer or a `%LocalAppData%` path layout. A test
asserts this.

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

---

## OPC UA Bridge

A headless OPC UA **client-to-server gateway**, installable as a Windows service.

The problem it solves: an application depends on an OPC UA server whose link
keeps dropping, and it has no good way to ride out the gaps. The bridge sits
between them. It holds one supervised session to that server — keep-alive
monitored, automatically reconnected, retried with backoff for as long as it
takes — and republishes the server's address space on its own `opc.tcp`
endpoint, with the same hierarchy, browse names and node identifiers. The
application changes one endpoint URL and otherwise keeps working.

During an outage the bridge does **not** drop its downstream clients. It keeps
serving the last known value for every tag with the status
`UncertainLastUsableValue` and its original source timestamp, so a client can see
both the value and how stale it is. After a configurable grace period (five
minutes by default) those degrade to `BadNoCommunication`.

### What it does

| | |
|---|---|
| **Resilient upstream** | One session, keep-alive monitored, reconnected by the SDK's reconnect handler, with a watchdog for keep-alives that simply stop arriving. Exponential backoff with jitter. |
| **Faithful mirror** | Same hierarchy, browse names and node identifiers as upstream; upstream namespace URIs registered as the bridge's own, so existing `ns=N;s=...` NodeIds keep resolving. |
| **Pinned namespace** | The address space is captured once to a JSON file and never changes until you explicitly capture it again — with a diff to confirm first. |
| **Two acquisition modes** | **Subscription** (the server reports changes; the default, and far cheaper for both ends) or **Polling** at an interval you set, for servers whose subscription support is unreliable. |
| **Write pass-through** | Downstream writes are forwarded upstream and the server's own status code is returned verbatim. Read-only upstream tags are read-only downstream. Nothing is ever queued. |
| **Built for thousands of tags** | Chunked subscriptions, batched reads sized to the server's own `MaxNodesPerRead`, allocation-free value publishing, and batched application to the mirror. |

### Installing

```powershell
# From an elevated PowerShell prompt, in the extracted publish folder:
.\deploy\Install-OpcUaBridge.ps1
```

This installs to `C:\OpcUaBridge` (not `Program Files` — the service writes
`Logs\`, `config\` and `pki\` beside its own executable), registers the
service to start automatically, configures it to restart after a crash, and
opens the firewall for the mirrored endpoint.

`.\deploy\Uninstall-OpcUaBridge.ps1` removes the service and leaves your
captured namespace and certificates in place; pass `-RemoveFiles` to delete
those too.

### Setting it up

**1. Point it at your server.** Edit `appsettings.json`:

```jsonc
"Bridge": {
  "Upstream": {
    "EndpointUrl": "opc.tcp://plant-server:4840",
    "SecurityMode": "SignAndEncrypt",
    "SecurityPolicy": "http://opcfoundation.org/UA/SecurityPolicy#Basic256Sha256",
    "AuthenticationType": "Anonymous"
  }
}
```

Then `Restart-Service OpcUaBridge`.

**2. Reuse the certificates you already have working.** If OPC UA Exporter
already connects to this server from this machine, the bridge can use the same
certificate and connect without anything changing on the server:

```powershell
& 'C:\OpcUaBridge\OpcUaBridge.exe' --import-exporter-certificates
```

This copies `%LocalAppData%\OpcUaExporter\pki` into `C:\OpcUaBridge\pki\client`.
Two things need to come across and both do: the client certificate the server
already trusts, and the server's own certificate that the exporter already
trusted.

This works because `Bridge:Upstream:ClientApplicationName` defaults to
`OpcUaExporter` rather than `OpcUaBridge` — the SDK finds its certificate by
subject name, so the bridge presents the identity the certificate was issued to.
Change that setting only if you would rather issue a fresh certificate and trust
it on the server.

Two caveats worth knowing:

- The certificate names the machine it was created on, so it is only accepted
  when the bridge runs on that same machine.
- Back up `pki\own` before the first run. If the identity does not match, the
  SDK quietly generates a replacement.

`OpcUaBridge.exe --check-pki` reports both certificate stores and what is in
them, which turns "it won't connect" into a five-second answer.

**3. Capture the namespace.** Open `http://127.0.0.1:5080` on the machine,
go to **Namespace**, and click **Browse and compare**. You get a summary of what
was found and, on later captures, a diff of what would change. Nothing is written
until you click **Apply**, and the previous snapshot is backed up first.

Restart the service to serve the new address space.

**4. Repoint your application** at
`opc.tcp://<machine>:4841/OpcUaBridge`.

### The dashboard

`http://127.0.0.1:5080`, loopback only by default — an unauthenticated admin UI
for a plant gateway has no business being reachable from the network. Binding
`Bridge:Web:Urls` anywhere else without setting `Bridge:Web:AdminToken` fails
startup rather than silently exposing it.

| Page | |
|---|---|
| **Dashboard** | Upstream state, reconnect count, last error; downstream endpoint; acquisition throughput; namespace age |
| **Tags** | Every mirrored tag with its live value, status and source timestamp, filterable |
| **Namespace** | Capture, diff, apply |
| **Diagnostics** | Recent OPC UA client activity, and where the log files are |

### Command line

```
OpcUaBridge.exe                                  Run the gateway
OpcUaBridge.exe --check-config                   Print the resolved configuration and paths
OpcUaBridge.exe --check-pki                      Report both certificate stores
OpcUaBridge.exe --import-exporter-certificates   Copy the exporter's certificates in
OpcUaBridge.exe --show-namespace                 Summarise the captured snapshot
```

Run without arguments from a console and it runs in the foreground, which is how
to debug it.

### Choosing an acquisition mode

**Subscription** is the default and almost always right: the server decides what
changed, so the bridge does no work when nothing is happening. At 5000 tags this
is five subscriptions of 1000 items at a one-second publishing interval.

**Polling** reads every tag on a timer. Use it when the server's subscription
support is absent or unreliable — often exactly why a gateway is needed. It costs
the upstream server considerably more: 5000 tags at one second means five reads
of 1000 nodes every second, whether anything changed or not.

```jsonc
"Acquisition": {
  "Mode": "Polling",
  "PollingIntervalMs": 1000
}
```

A polling cycle that is still running when the next is due is **skipped**, never
queued — queuing would turn a slow server into an unbounded backlog. The
dashboard counts skipped cycles, and that count rising is the signal that the
interval is too tight for the tag count.

### Where its state lives

Everything sits beside `OpcUaBridge.exe`, so the whole installation is one
portable folder:

```
C:\OpcUaBridge\
  OpcUaBridge.exe
  appsettings.json
  Logs\                    opcua-bridge.log — 30 MB per file, 10 files kept
  config\
    namespace.json         the captured address space
    namespace.<date>.json  backups, written before each capture
  pki\
    client\                the bridge as an OPC UA client, to your server
    server\                the bridge as an OPC UA server, to your application
```

The service account must be able to write all three directories — which is why
the installer defaults to `C:\OpcUaBridge` rather than `Program Files`.

### Why the namespace is a file

The saved snapshot is the contract between the bridge and the application behind
it. Pinning it means somebody reconfiguring the plant server cannot silently
change the tags your application sees; you find out when you choose to look, from
a diff.

It records namespace **URIs**, not just indexes. A namespace index is only
meaningful within the session that reported it, and servers do reorder their
namespace array across restarts. A snapshot keyed on bare indexes would, after
such a restart, serve completely different tags under the same names and report
them as `Good` — silent data corruption, and the worst failure a gateway can
have. Every NodeId is instead rebuilt from its URI on each new session, and any
namespace the server no longer publishes is reported on the dashboard.

### Limitations

- Only variables and folders are mirrored. Methods, events, alarms and
  historical access are not.
- Vendor-defined structured data types are mirrored as `BaseDataType` rather
  than reconstructed.
- Writes are forwarded, never queued. A write issued while the upstream link is
  down fails immediately with `BadNoCommunication`.
- The upstream server's standard `Server` object (its status and diagnostics) is
  not mirrored by default, since the bridge publishes its own. Set
  `Bridge:Snapshot:IncludeServerDiagnostics` if you want it.

## Prerequisites

- **Windows 10/11**
- **.NET 8 SDK** (https://dotnet.microsoft.com/download)

No other setup is required — the OPC UA client library is a standard NuGet dependency restored on build.

---

## Build & Run

```bash
dotnet build OpcUaExporter.sln                   # Windows: builds everything
dotnet run --project src/OpcUaExporter.Wpf       # the desktop exporter
dotnet run --project src/OpcUaBridge.Host        # the gateway, dashboard on :5080
```

Or open `OpcUaExporter.sln` in **Visual Studio 2022** and press **F5**.

**On Linux and macOS**, `OpcUaExporter.Wpf` targets `net8.0-windows` and cannot
build, so use the solution filter that excludes it:

```bash
dotnet build OpcUaExporter.Linux.slnf
```

Everything else, the gateway included, builds and runs on any OS.

### Tests

```bash
dotnet test tests/OpcUaExporter.Tests
dotnet test tests/OpcUaBridge.Tests --filter "Category!=Integration"
```

Both target `net8.0` and run on Windows, Linux and macOS alike.

The gateway's integration tests start a real OPC UA server in-process and
connect a real client to it — including one that stands a second server up as a
plant server and drives the whole loop: browse, capture, resolve, subscribe,
mirror. They are excluded by default because they are slow and generate
certificates:

```bash
dotnet test tests/OpcUaBridge.Tests --filter "Category=Integration"
```

---

## Usage

1. **Enter the server endpoint URL** (e.g. `opc.tcp://192.168.1.10:4840`) on the Tag Browser sidebar, or use **Discover Servers** to scan a host's ports for OPC UA endpoints.
2. Configure security mode/policy and authentication (anonymous or username/password) under **Connection Settings**, and click **Save** to add the profile to your saved-profiles library (a **Profile** dropdown appears once you have one, so you can switch between multiple servers — production, test, different machines/lines — without re-entering settings each time). **New**/**Duplicate**/**Delete** manage the library, and **Export…**/**Import…** move a single profile to/from an arbitrary file.
3. Click **Browse Tags** — the address space tree loads in the tag browser (top-level structure appears first, then the tree fills in as the deep scan continues; scanning can run in parallel — see `ConnectionProfile.EnableParallelBrowse`).
4. **Check** the tags you want (or use All / None / per-folder shortcuts).
5. Click **Read Values** for a one-off read, or **Subscribe** to get live updates pushed into the readings table.
6. With a live subscription active, optionally **record** updates to a CSV file and/or plot selected tags on the **live trend chart**.
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
│   └── OpcUaExporter.Wpf/           ← net8.0-windows · WinExe (OpcUaExporter.exe)
│       ├── App.xaml / App.xaml.cs   ← DI container, OnStartup, global crash logging
│       ├── MainWindow.xaml / .cs    ← WPF Window hosting BlazorWebView
│       ├── Services/
│       │   └── WpfFileDialogService.cs  ← IFileDialogService via Microsoft.Win32 dialogs
│       └── wwwroot/
│           ├── index.html           ← host page
│           └── app.ico
│
├── src/OpcUaShared/                 ← net8.0 · shared OPC UA client
│   ├── Configuration/               ← ApplicationConfiguration + identity
│   ├── Certificates/                ← trust store
│   ├── Sessions/                    ← endpoint selection, session creation
│   ├── Operations/                  ← browse, read, write, subscribe, node details
│   ├── Discovery/                   ← port scanning, endpoint capabilities
│   └── Services/OpcUaClientService.cs  ← create-use-dispose facade (the exporter's)
│
├── src/OpcUaBridge.Core/            ← net8.0 · the gateway
│   ├── BridgePaths.cs               ← every path, relative to the executable
│   ├── Configuration/               ← options, validation, DI
│   ├── Upstream/                    ← supervised session, reconnect, backoff
│   ├── Namespaces/                  ← snapshot model, store, capture, diff
│   ├── Tags/                        ← dense registry and value store
│   ├── Acquisition/                 ← subscription and polling engines
│   ├── Server/                      ← mirrored OPC UA server, write router
│   └── Certificates/                ← exporter certificate import
│
├── src/OpcUaBridge.Host/            ← net8.0 · the service (→ OpcUaBridge.exe)
│   ├── Program.cs                   ← Serilog, Windows service, Kestrel
│   ├── BridgeCommands.cs            ← --check-config, --check-pki, ...
│   ├── appsettings.json
│   └── Components/                  ← Blazor Server dashboard
│
├── deploy/                          ← Install/Uninstall-OpcUaBridge.ps1
│
└── tests/
    ├── OpcUaExporter.Tests/         ← net8.0 · xUnit
    └── OpcUaBridge.Tests/           ← net8.0 · xUnit, incl. live-server integration tests
```

---

## Security & Certificates

The exporter maintains its PKI store under `%LocalAppData%\OpcUaExporter\pki\` (`own`, `trusted`, `issuer`, `rejected` directories) and generates a client application certificate on first run. Untrusted server certificates are **not** auto-accepted or auto-rejected — they are surfaced in the UI so you can review and trust/reject them explicitly, after which the connection can be retried.

OPC UA Bridge keeps **two** stores beside its executable, because it acts in both roles: `pki\client\` for connecting to your server, and `pki\server\` for the clients that connect to it. They are separate key pairs on purpose — sharing one would mean a downstream client's trust decision also granted access to the identity your plant server knows. See [OPC UA Bridge](#opc-ua-bridge) for reusing the exporter's certificates.

Supported authentication: Anonymous and Username/Password. Security mode/policy (None, Sign, SignAndEncrypt with the standard OPC UA security policies) can be selected per connection profile, or discovered from the server via **Connection Settings → Discover Modes**.

---

## Publishing

```bash
dotnet publish src/OpcUaExporter.Wpf -c Release -r win-x64 --self-contained true
dotnet publish src/OpcUaBridge.Host  -c Release -r win-x64 --self-contained true
```

Both published outputs are self-contained — there is no separate runtime folder to copy alongside them.

Publish the bridge to a plain folder, not a single file: it resolves its logs,
configuration and certificates from the directory its executable sits in, and
single-file publishing changes what that directory is.

---

## Troubleshooting

| Symptom | Fix |
|---|---|
| Blank WebView / `blazor.webview.js` 404 | Ensure `Microsoft.AspNetCore.Components.WebView.Wpf` NuGet is installed |
| Connection timeout | Check firewall, confirm OPC UA server is running and the port is open |
| `No endpoint matches SecurityMode=... and SecurityPolicy=...` | Use **Discover Modes** on the Connection Settings page to see the security options the server actually offers, then match your profile to one of them |
| Certificate errors on connect | Check the **Server Certificates** panel on the Tag Browser sidebar for a pending certificate to trust/reject |
| `Client application certificate key size is ...` on startup | Delete `%LocalAppData%\OpcUaExporter\pki\own` and restart the app to regenerate a stronger certificate |
