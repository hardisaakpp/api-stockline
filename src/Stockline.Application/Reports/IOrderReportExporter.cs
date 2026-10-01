namespace Stockline.Application.Reports;

public interface IOrderReportExporter
{
    byte[] Export(MarginReport report);

    byte[] Export(DifferencesReport report);

    byte[] Export(TransfersReport report);

    byte[] Export(ErpIntegrationReport report);
}
