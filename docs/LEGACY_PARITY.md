# Parity with the legacy system

Stockline re-implements the business logic of a larger Python/FastAPI system (the *legacy system* below). This document
records how the two were compared, what was migrated as-is, what was changed on purpose, and what was left out.

## How the comparison was done

The legacy source was read module by module — validators, services, repositories, endpoints, e-mail templates, report
generators and the Celery tasks — and every rule found was checked against this code base:

| Legacy area | Read |
|---|---|
| Template parsing and validation (header, price, quantity, store and SAP validators) | ✔ |
| Order states, transitions and who may trigger them | ✔ |
| Order editing (cost / total / comment), audit log, role-based data hiding | ✔ |
| Supplier flow: confirmation, production difference, child orders | ✔ |
| Redistribution: validator, draft, initial proportional distribution, store catalog | ✔ |
| ERP integration: purchase-order and transfer-request builders, pre-validation, retry and reconciliation tasks | ✔ |
| Reports: margin, differences, transfers; purchase-order PDF | ✔ |
| E-mail notifications and the real-time stream | ✔ |
| Listings, filters, last-order lookups, user preferences, file storage | ✔ |

Every gap found was either fixed (with tests) or recorded below as a deliberate decision.

## Status vocabulary mapping

| Legacy status | Stockline status |
|---|---|
| Draft | `Draft` |
| New order | `Submitted` |
| Under management | `UnderReview` |
| Confirmed by production | `ProductionConfirmed` |
| Confirmed by the supplier | `SupplierConfirmed` |
| Production postponed (child order) | `ProductionPostponed` |
| Ready for redistribution (accepted child) | `ReadyForRedistribution` |
| Redistribution confirmed | `RedistributionConfirmed` |
| ERP retry error / master-data error | `ErpSubmissionFailed` (the message tells which) |
| Logistics pending | `LogisticsPending` |
| Confirmed and sent | `Sent` |
| Dispatch and invoice | `DispatchAndInvoice` |
| Finished | `Completed` |
| Cancelled by the buyer | `Cancelled` |
| Cancelled by the supplier | `CancelledBySupplier` |
| Rejected (postponed child) | `Rejected` |

Three legacy statuses (supplier response loaded, supplier confirmed, redistributed) were dropped because nothing reaches
them any more: a supplier-approval flow that was never wired to an endpoint and whose statuses were deactivated by the
legacy migrations.

## Migrated

**Import and validation**
- Matrix template (header sheet + product × location quantities), totals / subtotal / unit-cost consistency checks.
- Header: required fields, 100-character limits, order-date-older-than-a-year warning, delivery date not in the past.
- Product code: 1–50 characters of letters, digits, dots, hyphens and spaces; duplicate products and locations.
- Prices: negative → error; zero cost / zero retail price / discount above 50% / retail price below cost with tax and
  discount → warnings; discounts accepted as a fraction (`0.2`) or a percentage (`20`).
- Quantity sanity warnings (10,000 per location, 100,000 per product).
- Products validated against the ERP at import time (exists, active, pack and retail price match) with suggestions for
  unknown codes and buyer-supplied corrections — now optional (`validateErp`) and with a dry-run endpoint that can return
  the validation log as an Excel file.
- The original uploaded file is kept and can be downloaded again (database blob instead of object storage).

**Workflow**
- All transitions and their permissions; cancel allowed only before production is confirmed; a postponed child can be
  accepted or rejected, never cancelled; discard (hard delete) for draft / submitted orders.
- Supplier edits: confirmed quantity, cost and comment while the order is under review or confirmed by production;
  warning when a confirmation is under 80% of the request or a cost rises more than 10%; cost ≤ 1,000,000.
- Production difference: acknowledge it or split it into whole-unit child orders `<code>.NN` that add up exactly.
- Redistribution: totals must equal the confirmed quantity, no negatives, locations must belong to the order, no
  duplicates; child orders may use any active store of the catalog; proportional initial distribution with the last
  location absorbing the rounding; saved draft.
- Field-level audit log (who, when, old and new value), creator / last modifier tracking.
- Role-based data protection: production never sees costs; retail prices are buyer-only; production only sees orders in
  *Submitted* and *Under review*.

**ERP**
- Purchase order and one transfer request per store, master-data check before anything is created, due date never earlier
  than the document date, idempotent creation, transient vs permanent errors, automatic retry of transient ones, and a log
  of every attempt (`/erp/attempts`) with a cross-order document list (`/erp/documents`, pending-retry filter included).
- Correcting a rejected product code and re-planning the unsent documents.
- Purchase-order PDF (header, lines, financial summary, signature block) attached to the "ready for dispatch" e-mail.
- ERP integration workbook per order.

**Reports**
- Margin: `1 − unit cost / (retail price / 1.15)`, buyer-only, negatives highlighted.
- Differences: per product (requested / confirmed / allocated, original vs confirmed cost) and per location.
- Transfers: per store and product with the ERP transfer number, plus the matrix by location.

**Notifications and misc**
- Role-filtered live event stream; subscription-based e-mails per topic and audience; e-mail preview; admin status.
- Listing filters (status, dates, supplier, creator, modifier, "pending my action"), filter options, order-code
  existence check, last created / last redistributed order, location search, per-user UI preferences.

## Changed on purpose

| Legacy behaviour | Stockline | Why |
|---|---|---|
| The confirmed total could exceed the requested quantity (up to 10 million) | Confirmed quantity is limited to `0…requested` | The supplier-response rules of the legacy system already required it, and redistribution assumes it. |
| Production could edit costs it could not see | Only the supplier edits costs | Editing blind values is not a feature. |
| One purchase order per order, warehouse = first warehouse with stock, falling back to the transfer origin | One purchase order **per supplying warehouse**, one transfer request per store | Mixed orders (stores supplied by different warehouses) were not representable. |
| Company-specific user-defined fields on the purchase order and a `P` prefix on supplier codes | Supplier resolved by name; documents carry a comment and an idempotency key | Those fields belong to one company's ERP configuration. |
| E-mail catalog stored in the database, types created at runtime | Fixed set of topics in code, subscriptions in the database | The set of events is part of the application, not of the data. |
| Several template layouts auto-detected, store modes (strict / create / sync master) | One matrix layout; the store catalog is administered through `/admin/locations` | Simpler contract; the catalog has an explicit owner. |
| A product code is corrected on the generated purchase-order line (with an ERP lookup) | `PUT …/lines/{sku}/product-code` corrects the **order** line while the submission is failed and the unsent documents are planned again | Documents are derived data in this design, so the source of truth is fixed instead of the copy. |
| Background processing through Celery + Redis, progress streamed per process id | A durable job table claimed by workers with a lease (any number of instances), with per-job progress at `GET /jobs/{id}`; the live SSE stream stays for order events | Same goal (many users at once, heavy work off the request) without a broker to operate; jobs survive restarts. |
| Status ids (numeric, order-sensitive comparisons such as `< 10`) | Named statuses and an explicit transition table | Removes a class of bugs: new statuses silently fell inside or outside numeric ranges. |

## Not migrated

| Legacy feature | Reason |
|---|---|
| Dispatch/invoice migration flow, remissions, product cross-matching and SKU homologation against a second legacy ERP | A different integration that is out of scope; products are validated against the ERP directly. |
| Celery beat reconciliation tasks (query the ERP to find documents whose response was lost) | Idempotent creation by key makes reconciliation unnecessary: a retry finds the document it already created. |
| Capacity-warning, migration and homologation e-mails | Belong to the legacy infrastructure above. |
| "Text file for the second ERP" export, MBA-format flat file | Part of the same out-of-scope integration. |
| Hard delete of purchase-order records and bulk delete | Deleting documents that exist in the ERP is not something an API should offer. |
| Column-highlight persistence ("modified columns") for the legacy web client | Presentation state; the audit log has the underlying facts. |

## Differences worth knowing

- The differences report shows the **current** allocation per location. The legacy report also compared the original
  per-store request, which this model does not keep once the buyer confirms a redistribution.
- Failed attempts of documents that are re-planned after a product-code correction are discarded together with the
  documents; the correction itself is in the audit log.
- Email attachments (PDF) are generated when the e-mail is sent, not stored.
