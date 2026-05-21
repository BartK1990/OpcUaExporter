# OPC UA Exporter

A Windows desktop application built with **Blazor Hybrid (WinForms)** that connects to OPC UA servers, browses their tag trees, reads live values, and exports selected tags to CSV or JSON.

OPC UA communication is handled by an **embedded Python runtime** using the free [`opcua`](https://github.com/FreeOpcUa/python-opcua) (python-opcua) library, called from .NET as a child process.

---

## Architecture

```
┌──────────────────────────────────────────────────────────┐
│  WPF Host (MainWindow.xaml)                              │
│  ┌────────────────────────────────────────────────────┐  │
│  │  BlazorWebView                                     │  │
│  │  ┌──────────────────────────────────────────────┐  │  │
│  │  │  Blazor Components  (Index.razor, TagNode)   │  │  │
│  │  │           ↕ DI                               │  │  │
│  │  │  OpcUaService  ──►  PythonBridgeService      │  │  │
│  │  └──────────────────────────────────────────────┘  │  │
│  └────────────────────────────────────────────────────┘  │
│                          │ Process.Start()               │
│                          ▼                               │
│          Python\runtime\python.exe                       │
│          Python\opc_ua_bridge.py  ──► OPC UA Server     │
│              (python-opcua library)                      │
└──────────────────────────────────────────────────────────┘
```

### Key design decisions

| Layer | Technology | Why |
|---|---|---|
| UI host | WPF + BlazorWebView | Blazor Hybrid on Windows, native file dialogs, XAML layout |
| UI components | Razor + CSS | Modern reactive UI, no WinForms designer lock-in |
| OPC UA | python-opcua (free) | Mature, free OPC UA client; no licence cost |
| Python runtime | Embedded CPython 3.11 | Self-contained; no system Python required |
| .NET → Python | `System.Diagnostics.Process` | Simple, robust; JSON over stdout/stderr |

---

## Prerequisites

- **Windows 10/11**
- **.NET 8 SDK** (https://dotnet.microsoft.com/download)
- **PowerShell 5+** (included in Windows)
- Internet access for the one-time setup

---

## One-time setup — Embedded Python Runtime

Run the bootstrap script **once** from the solution root:

```powershell
powershell -ExecutionPolicy Bypass -File setup_python_runtime.ps1
```

This script:
1. Downloads **Python 3.11 embeddable zip** (≈ 10 MB) from python.org
2. Extracts it to `OpcUaExporter\Python\runtime\`
3. Patches `python311._pth` to enable `site-packages`
4. Installs `pip` and then `opcua` into the runtime

After this step, `Python\runtime\python.exe` is fully self-contained — no system Python is used at runtime.

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

1. **Enter the server endpoint URL** (e.g. `opc.tcp://192.168.1.10:4840`)
2. Click **Browse Tags** — the address space tree loads in the right panel
3. **Check** the tags you want (or use All / None shortcuts)
4. Click **Read Values** to fetch live values into the table at the bottom
5. Choose a **format** (CSV or JSON), enter or browse for an **output path**
6. Click **Export Selected**

---

## Project Structure

```
OpcUaExporter.sln
setup_python_runtime.ps1        ← one-time bootstrap
OpcUaExporter/
├── OpcUaExporter.csproj
├── App.xaml                    ← WPF Application definition (no StartupUri)
├── App.xaml.cs                 ← DI container setup + OnStartup
├── MainWindow.xaml             ← WPF Window hosting BlazorWebView
├── MainWindow.xaml.cs          ← JS-invokable SaveFileDialog (Microsoft.Win32)
│
├── Models/
│   └── OpcModels.cs            ← OpcTag, TagReading, ConnectionProfile, ExportOptions
│
├── Services/
│   ├── PythonBridgeService.cs  ← launches python.exe, parses JSON output
│   └── OpcUaService.cs         ← high-level state management for Blazor
│
├── Components/
│   ├── App.razor
│   ├── _Imports.razor
│   ├── TagNode.razor           ← recursive tag tree component
│   └── Pages/
│       └── Index.razor         ← main page (sidebar + content)
│
├── Python/
│   ├── opc_ua_bridge.py        ← Python OPC UA script (browse / read / export)
│   └── runtime/                ← embedded CPython (created by setup script)
│
└── wwwroot/
    ├── index.html
    └── css/
        └── app.css
```

---

## Python Bridge Protocol

The bridge is invoked as a subprocess:

```
python.exe opc_ua_bridge.py <command> [args...]
```

| Command | Args | Stdout |
|---|---|---|
| `browse` | `<endpoint_url>` | `{"ok": true, "tags": [...]}` |
| `read` | `<endpoint_url>` `<node_ids_json>` | `{"ok": true, "rows": [...]}` |
| `export` | `<endpoint_url>` `<node_ids_json>` `<path>` | `{"ok": true, "exported": N, "path": "..."}` |

Errors are returned as `{"error": "...", "trace": "..."}` on stderr with exit code 1.

---

## Customisation

### Security / Authentication
Edit `opc_ua_bridge.py` → `connect()` to add username/password or certificate auth:

```python
def connect(endpoint_url: str) -> Client:
    client = Client(endpoint_url)
    client.set_user("admin")
    client.set_password("secret")
    client.connect()
    return client
```

### Subscriptions / Live monitoring
Add a `monitor` command to the Python script using `opcua.Subscription`.

### Publishing
```bash
dotnet publish -c Release -r win-x64 --self-contained true
```
Include the `Python\` folder alongside the published output.

---

## Troubleshooting

| Symptom | Fix |
|---|---|
| `Embedded Python runtime not found` | Run `setup_python_runtime.ps1` |
| `opcua library not found` | Run `python.exe -m pip install opcua` inside `Python\runtime\` |
| Blank WebView / `blazor.webview.js` 404 | Ensure `Microsoft.AspNetCore.Components.WebView.Wpf` NuGet is installed |
| Connection timeout | Check firewall, confirm OPC UA server is running and port is open |
