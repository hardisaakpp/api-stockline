using Stockline.Domain.Notifications;
using Stockline.Domain.Orders;

namespace Stockline.Application.Notifications;

public sealed record EmailTopicInfo(EmailTopic Topic, EmailAudience Audience, string Subject, string Description);

public static class EmailTopics
{
    public static IReadOnlyList<EmailTopicInfo> All { get; } =
    [
        new(EmailTopic.OrderSubmitted, EmailAudience.SupplierProduction, "Order submitted", "The buyer sent a new order to the supplier; production has to review it."),
        new(EmailTopic.OrderCancelledByBuyer, EmailAudience.Supplier, "Order cancelled by the buyer", "The buyer cancelled an order."),
        new(EmailTopic.OrderRejected, EmailAudience.Supplier, "Postponed order rejected", "The buyer rejected a postponed child order."),
        new(EmailTopic.OrderDiscarded, EmailAudience.Supplier, "Order discarded", "The buyer discarded an order that had already been submitted."),
        new(EmailTopic.OrderCancelledBySupplier, EmailAudience.Buyer, "Order cancelled by the supplier", "The supplier cancelled an order after confirming production."),
        new(EmailTopic.ProductionConfirmed, EmailAudience.Supplier, "Order confirmed by production", "Supplier production confirmed an order; the supplier administration can now review it."),
        new(EmailTopic.RedistributionConfirmed, EmailAudience.Supplier, "Redistribution confirmed", "The buyer confirmed the redistribution of an order."),
        new(EmailTopic.ReadyForDispatch, EmailAudience.Supplier, "Purchase order and transfers ready for dispatch", "The ERP documents of an order were created."),
        new(EmailTopic.ErpSubmissionFailed, EmailAudience.Technical, "ERP submission failed", "The ERP rejected or could not receive an order's documents."),
    ];

    public static EmailTopicInfo Info(EmailTopic topic) => All.Single(t => t.Topic == topic);

    /// <summary>The email topic an order event triggers, or null when the event sends no email.</summary>
    public static EmailTopic? For(OrderEvent orderEvent) => orderEvent.Type switch
    {
        nameof(OrderAction.Submit) => EmailTopic.OrderSubmitted,
        nameof(OrderAction.Cancel) => EmailTopic.OrderCancelledByBuyer,
        nameof(OrderAction.Reject) => EmailTopic.OrderRejected,
        nameof(OrderAction.CancelBySupplier) => EmailTopic.OrderCancelledBySupplier,
        nameof(OrderAction.ConfirmProduction) => EmailTopic.ProductionConfirmed,
        nameof(OrderAction.ConfirmRedistribution) => EmailTopic.RedistributionConfirmed,
        "OrderDiscarded" when orderEvent.Status == nameof(OrderStatus.Submitted) => EmailTopic.OrderDiscarded,
        "ErpSubmission" when orderEvent.Status == nameof(OrderStatus.ErpSubmissionFailed) => EmailTopic.ErpSubmissionFailed,
        "ErpSubmission" when orderEvent.Status is nameof(OrderStatus.Sent) or nameof(OrderStatus.LogisticsPending) => EmailTopic.ReadyForDispatch,
        _ => null,
    };
}
