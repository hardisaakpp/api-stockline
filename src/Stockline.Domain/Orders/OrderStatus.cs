namespace Stockline.Domain.Orders;

public enum OrderStatus
{
    Draft,
    Submitted,
    UnderReview,
    ProductionConfirmed,
    SupplierConfirmed,
    ProductionPostponed,
    ReadyForRedistribution,
    RedistributionConfirmed,
    ErpSubmissionFailed,
    LogisticsPending,
    Sent,
    DispatchAndInvoice,
    Completed,
    Rejected,
    Cancelled,
    CancelledBySupplier,
}
