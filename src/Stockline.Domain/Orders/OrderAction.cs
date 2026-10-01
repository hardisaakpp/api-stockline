namespace Stockline.Domain.Orders;

public enum OrderAction
{
    Submit,
    Open,
    ConfirmProduction,
    ConfirmBySupplier,
    CancelBySupplier,
    AcceptPostponed,
    Reject,
    Cancel,
    ConfirmRedistribution,
    MarkSent,
    MarkLogisticsPending,
    MarkErpSubmissionFailed,
    MarkDispatchAndInvoice,
    Complete,
}
