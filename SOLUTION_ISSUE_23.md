# Solution for Issue #23

## 🛠️ Proposed Solution (by Aditya Waghamare)

### Analysis
The OpcUaExporter tool currently supports exporting data to CSV and JSON formats. To add direct Excel (`.xlsx`) export functionality without adding heavy or restrictive dependencies, the `OfficeIMO.Word` / `OfficeIMO.Excel` library (MIT license) can be integrated. Alternatively, standard patterns for data export configuration, format enums, and exporter service mapping need to be updated.

### Fix
Update the exporter configuration, formats enum, and export handler to support `Excel` / `.xlsx` output using `OfficeIMO` or `ClosedXML` / `EPPlus` / `OfficeIMO.Excel`. Below is the implementation demonstrating the addition of the Excel export option and exporter service logic.

### Implementation
```csharp
// 1. Add Excel to ExportFormat enum
public enum ExportFormat
{
    Csv,
    Json,
    Excel
}

// 2. Implement Excel Exporter service using OfficeIMO / OpenXML approach
using OfficeIMO.Excel;

public class ExcelExporter : IDataExporter
{
    public async Task ExportAsync(IEnumerable<OpcUaDataRecord> records, string filePath)
    {
        using (var document = ExcelDocument.Create(filePath))
        {
            var sheet = document.AddWorksheet("OpcUa Data");
            
            // Add Headers
            sheet.Rows[1][1].Value = "Timestamp";
            sheet.Rows[1][2].Value = "NodeId";
            sheet.Rows[1][3].Value = "Value";
            sheet.Rows[1][4].Value = "Status";

            int rowIdx = 2;
            foreach (var record in records)
            {
                sheet.Rows[rowIdx][1].Value = record.Timestamp.ToString("o");
                sheet.Rows[rowIdx][2].Value = record.NodeId;
                sheet.Rows[rowIdx][3].Value = record.Value?.ToString();
                sheet.Rows[rowIdx][4].Value = record.Status;
                rowIdx++;
            }

            document.Save();
        }
    }
}

// 3. Register in Exporter Factory / Service Collection
services.AddTransient<ExcelExporter>();
```

### Testing
1. Configure exporter format to `Excel`.
2. Run export command with sample OPC UA node records.
3. Verify output `.xlsx` file is generated correctly with proper headers and data rows.

Signed-off-by: Aditya Waghamare <adityawaghamare7620@gmail.com>

---
*Submitted by Aditya Waghamare*
💰 **Payout Address (Base L2 / EVM):** `0xb61dBcdBc3407F71EaCb64D4CBFAcf9FFfe2415C`