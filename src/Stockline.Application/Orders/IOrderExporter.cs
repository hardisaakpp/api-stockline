using Stockline.Domain.Orders;

namespace Stockline.Application.Orders;

public interface IOrderExporter
{
    /// <summary>Costs, discounts and retail prices are left blank for roles that must not see them.</summary>
    byte[] ExportTemplate(Order order, ActorRole viewer);
}
