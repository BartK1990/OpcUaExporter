using Microsoft.Extensions.Logging;
using OpcUaExporter.Models;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace OpcUaExporter.Services;

/// <summary>
/// Executes the embedded Python bridge script as a child process and
/// deserialises its JSON output.
/// </summary>
public class PythonBridgeService
{
    private readonly ILogger<PythonBridgeService> _logger;
    private readonly string _pythonExe;
    private readonly string _scriptPath;
    private readonly string[] _searchedBaseDirs;

    // JSON options – case-insensitive to match Python output
    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public PythonBridgeService(ILogger<PythonBridgeService> logger)
    {
        _logger = logger;

        var baseDirs = GetCandidateBaseDirectories().ToArray();
        _searchedBaseDirs = baseDirs;

        _pythonExe  = ResolveExistingPath(baseDirs, Path.Combine("Python", "runtime", "python.exe"))
                      ?? Path.Combine(AppContext.BaseDirectory, "Python", "runtime", "python.exe");
        _scriptPath = ResolveExistingPath(baseDirs, Path.Combine("Python", "opc_ua_bridge.py"))
                      ?? Path.Combine(AppContext.BaseDirectory, "Python", "opc_ua_bridge.py");

        _logger.LogInformation("Python exe  : {Exe}",    _pythonExe);
        _logger.LogInformation("Bridge script: {Script}", _scriptPath);
    }

    // -----------------------------------------------------------------------
    // Public API
    // -----------------------------------------------------------------------

    /// <summary>Browse the OPC UA server address space.</summary>
    public async Task<List<OpcTag>> BrowseAsync(string endpointUrl,
                                                 CancellationToken ct = default)
    {
        var result = await RunAsync(ct, "browse", endpointUrl);
        ValidateOk(result);

        var tags = result.GetProperty("tags").Deserialize<List<OpcTag>>(_json)
                   ?? new List<OpcTag>();
        return tags;
    }

    /// <summary>Read live values for a set of node IDs.</summary>
    public async Task<List<TagReading>> ReadAsync(string endpointUrl,
                                                   IEnumerable<string> nodeIds,
                                                   CancellationToken ct = default)
    {
        var idsJson = JsonSerializer.Serialize(nodeIds.ToList());
        var result  = await RunAsync(ct, "read", endpointUrl, idsJson);
        ValidateOk(result);

        return result.GetProperty("rows").Deserialize<List<TagReading>>(_json)
               ?? new List<TagReading>();
    }

    /// <summary>Export selected tags to a file.</summary>
    public async Task<string> ExportAsync(string endpointUrl,
                                           IEnumerable<string> nodeIds,
                                           ExportOptions options,
                                           CancellationToken ct = default)
    {
        var idsJson = JsonSerializer.Serialize(nodeIds.ToList());
        var result  = await RunAsync(ct, "export", endpointUrl, idsJson, options.OutputPath);
        ValidateOk(result);

        return result.GetProperty("path").GetString() ?? options.OutputPath;
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private void ValidateOk(JsonElement root)
    {
        if (root.TryGetProperty("error", out var err))
        {
            var trace = root.TryGetProperty("trace", out var t) ? t.GetString() : null;
            var msg   = err.GetString() ?? "Unknown Python error";
            _logger.LogError("Python bridge error: {Msg}\n{Trace}", msg, trace);
            throw new InvalidOperationException(msg);
        }
    }

    private async Task<JsonElement> RunAsync(CancellationToken ct, params string[] args)
    {
        if (!File.Exists(_pythonExe))
            throw new FileNotFoundException(
                $"Embedded Python runtime not found at: {_pythonExe}\n" +
                $"Searched base directories: {string.Join("; ", _searchedBaseDirs)}\n" +
                "Run setup_python_runtime.ps1 to install it.", _pythonExe);

        if (!File.Exists(_scriptPath))
            throw new FileNotFoundException(
                $"Python bridge script not found at: {_scriptPath}", _scriptPath);

        // Build argument string – quote each arg to handle spaces/JSON
        var quotedArgs = string.Join(" ",
            args.Prepend(_scriptPath).Select(a => $"\"{a.Replace("\"", "\\\"")}\""));

        _logger.LogDebug("Running: {Exe} {Args}", _pythonExe, quotedArgs);

        var psi = new ProcessStartInfo
        {
            FileName               = _pythonExe,
            Arguments              = quotedArgs,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true
        };

        using var process = new Process { StartInfo = psi };

        var stdoutBuilder = new StringBuilder();
        var stderrBuilder = new StringBuilder();

        process.OutputDataReceived += (_, e) => { if (e.Data != null) stdoutBuilder.AppendLine(e.Data); };
        process.ErrorDataReceived  += (_, e) => { if (e.Data != null) stderrBuilder.AppendLine(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await process.WaitForExitAsync(ct);

        var stdout = stdoutBuilder.ToString().Trim();
        var stderr = stderrBuilder.ToString().Trim();

        if (!string.IsNullOrEmpty(stderr))
            _logger.LogWarning("Python stderr: {Err}", stderr);

        _logger.LogDebug("Python stdout: {Out}", stdout);

        if (string.IsNullOrWhiteSpace(stdout))
        {
            // Try to surface stderr as the error message
            var errorMsg = string.IsNullOrEmpty(stderr)
                ? "Python script produced no output."
                : stderr;
            throw new InvalidOperationException(errorMsg);
        }

        try
        {
            return JsonDocument.Parse(stdout).RootElement;
        }
        catch (JsonException je)
        {
            _logger.LogError(je, "Failed to parse Python output: {Out}", stdout);
            throw new InvalidOperationException($"Invalid JSON from Python bridge: {stdout}", je);
        }
    }

    private static IEnumerable<string> GetCandidateBaseDirectories()
    {
        var roots = new List<string>();

        void AddRoot(string? path)
        {
            if (!string.IsNullOrWhiteSpace(path))
                roots.Add(Path.GetFullPath(path));
        }

        AddRoot(AppContext.BaseDirectory);
        AddRoot(Directory.GetCurrentDirectory());
        AddRoot(Path.GetDirectoryName(Environment.ProcessPath));

        var all = new List<string>();
        foreach (var root in roots)
        {
            var dir = root;
            for (var i = 0; i < 4 && !string.IsNullOrEmpty(dir); i++)
            {
                all.Add(dir);
                dir = Directory.GetParent(dir)?.FullName;
            }
        }

        return all.Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static string? ResolveExistingPath(IEnumerable<string> baseDirs, string relativePath)
    {
        foreach (var baseDir in baseDirs)
        {
            var candidate = Path.Combine(baseDir, relativePath);
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }
}
