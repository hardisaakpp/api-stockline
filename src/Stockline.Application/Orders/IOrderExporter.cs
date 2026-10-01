using Stockline.Domain.Orders;

namespace Stockline.Application.Orders;

public interface IOrderExporter
{
    byte[] ExportTemplate(Order order);
}
