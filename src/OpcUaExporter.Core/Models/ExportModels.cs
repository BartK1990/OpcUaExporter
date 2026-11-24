namespace OpcUaExporter.Models;

/// <summary>Export options.</summary>
public class ExportOptions
{
    public string OutputPath  { get; set; } = string.Empty;
    public ExportFormat Format { get; set; } = ExportFormat.Csv;

    /// <summary>
    /// Adds Value, Quality and Timestamp columns holding each tag's last read (or subscribed) value.
    /// Tags that have not been read yet get empty cells; nothing is read from the server for the export.
    /// </summary>
    public bool IncludeLastValues { get; set; }
}

public enum ExportFormat { Csv, Json, Xlsx }
