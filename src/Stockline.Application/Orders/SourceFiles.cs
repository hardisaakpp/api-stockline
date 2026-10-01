using Stockline.Domain.Orders;

namespace Stockline.Application.Orders;

public interface IOrderFileStore
{
    Task SaveAsync(OrderSourceFile file, CancellationToken ct);

    Task<OrderSourceFile?> GetAsync(Guid orderId, CancellationToken ct);
}
