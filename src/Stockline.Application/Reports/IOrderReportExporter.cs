using Stockline.Application.Orders.Import;

namespace Stockline.Application.Reports;

public interface IOrderReportExporter
{
    byte[] Export(MarginReport report);

    byte[] Export(DifferencesReport report);

    byte[] Export(TransfersReport report);

    byte[] Export(ErpIntegrationReport report);

    /// <summary>The issues found while validating a template, as a workbook the buyer can send back to the supplier.</summary>
    byte[] ExportValidationLog(IReadOnlyList<ImportIssue> issues);
}
