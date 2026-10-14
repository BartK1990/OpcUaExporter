namespace OpcUaExporter.Models;

/// <summary>Export options.</summary>
public class ExportOptions
{
    public string OutputPath  { get; set; } = string.Empty;
    public ExportFormat Format { get; set; } = ExportFormat.Csv;
}

public enum ExportFormat { Csv, Json, Xlsx }
