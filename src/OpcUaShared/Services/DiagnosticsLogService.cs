using System.Collections.Concurrent;

namespace OpcUaExporter.Services;

public class DiagnosticsLogService
{
    private const int MaxEntries = 500;
    private readonly ConcurrentQueue<string> _entries = new();

    public event Action? Changed;

    public IReadOnlyList<string> Entries => _entries.ToArray();

    public void Add(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return;

        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        _entries.Enqueue(line);

        while (_entries.Count > MaxEntries && _entries.TryDequeue(out _))
        {
        }

        Changed?.Invoke();
    }

    public void Clear()
    {
        while (_entries.TryDequeue(out _))
        {
        }

        Changed?.Invoke();
    }
}
