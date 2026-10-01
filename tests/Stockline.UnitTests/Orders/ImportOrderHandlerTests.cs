using Stockline.UnitTests.Support;
using ClosedXML.Excel;
using Stockline.Application.Orders;
using Stockline.Application.Orders.Import;
using Stockline.Domain.Orders;
using Stockline.Infrastructure.Excel;

namespace Stockline.UnitTests.Orders;

public class ImportOrderHandlerTests
{
    private sealed class FakeReader(Func<OrderTemplate> read) : IOrderTemplateReader
    {
        public OrderTemplate Read(Stream content) => read();
    }

    private sealed class FakeCatalogs : ILocationCatalogProvider
    {
        public Task<LocationCatalog> GetAsync(CancellationToken ct) => Task.FromResult(new LocationCatalog(
            new Dictionary<string, LocationKind> { ["WH-1"] = LocationKind.Warehouse, ["ST-1"] = LocationKind.Store }));
    }

    private sealed class InMemoryOrders : IOrderRepository
    {
        public List<Order> Saved { get; } = [];

        public Task<bool> ExistsAsync(string orderCode, CancellationToken ct) =>
            Task.FromResult(Saved.Any(o => o.Header.Code == orderCode));

        public Task AddAsync(Order order, CancellationToken ct)
        {
            Saved.Add(order);
            return Task.CompletedTask;
        }

        public Task RemoveAsync(Order order, CancellationToken ct) => Task.CompletedTask;

        public Task<Order?> GetAsync(Guid id, CancellationToken ct) =>
            Task.FromResult(Saved.FirstOrDefault(o => o.Id == id));

        public Task<Order?> GetForUpdateAsync(Guid id, CancellationToken ct) => GetAsync(id, ct);

        public Task SaveChangesAsync(CancellationToken ct) => Task.CompletedTask;

        public void Stage(Order order) => Saved.Add(order);

        public Task<int> CountChildrenAsync(Guid parentOrderId, CancellationToken ct) => Task.FromResult(0);
    }

    private static readonly OrderTemplateHeader Header = new(
        " PO-1 ", new DateOnly(2026, 9, 24), null, "IMMEDIATE", "120 DAYS", "hello", "ACME", "BUYER", "801", "WH-1", "10");

    private static OrderTemplateLine Line(string product, params LocationQuantity[] quantities) =>
        new(3, product, "123", "Item", "X6", 5.6m, 2.8m, 0.2m, 12.99m, quantities.Sum(q => q.Quantity),
            quantities.Where(q => q.LocationCode != "WH-1").Sum(q => q.Quantity), quantities);

    private static ImportOrderHandler Handler(Func<OrderTemplate> template, InMemoryOrders orders) =>
        new(new FakeReader(template), FixedTimeProvider.Validator(), new FakeCatalogs(), orders);

    private static OrderTemplate ValidTemplate() => new(
        Header, [Line("SKU-1", new("WH-1", "Warehouse", 2m), new("ST-1", "Store one", 1m))], []);

    [Fact]
    public async Task Valid_template_is_imported_as_a_draft_order_with_mapped_lines()
    {
        var orders = new InMemoryOrders();

        var result = await Handler(ValidTemplate, orders).HandleAsync(Stream.Null, default);

        Assert.Equal(ImportOutcome.Imported, result.Outcome);
        var order = Assert.Single(orders.Saved);
        Assert.Equal(result.OrderId, order.Id);
        Assert.Equal("PO-1", order.Header.Code);
        Assert.Equal("ACME", order.Header.SupplierName);
        Assert.Equal(OrderStatus.Draft, order.Status);
        var line = Assert.Single(order.Lines);
        Assert.Equal(new Pack(6), line.Pack);
        Assert.Equal(0.2m, line.Discount);
        Assert.Equal(
            [LocationKind.Warehouse, LocationKind.Store],
            line.Allocations.Select(a => a.Kind));
    }

    [Fact]
    public async Task Lines_without_quantities_are_skipped_and_reported_as_warnings()
    {
        var orders = new InMemoryOrders();
        var template = ValidTemplate() with { Lines = [.. ValidTemplate().Lines, Line("SKU-EMPTY")] };

        var result = await Handler(() => template, orders).HandleAsync(Stream.Null, default);

        Assert.Equal(ImportOutcome.Imported, result.Outcome);
        Assert.Equal("SKU-1", Assert.Single(Assert.Single(orders.Saved).Lines).ProductCode);
        Assert.Equal("LINE_WITHOUT_QUANTITIES", Assert.Single(result.Issues).Code);
    }

    [Fact]
    public async Task Unreadable_file_is_reported_as_invalid_format()
    {
        var orders = new InMemoryOrders();
        var handler = new ImportOrderHandler(
            new FakeReader(() => throw new InvalidOrderTemplateException("bad file")),
            FixedTimeProvider.Validator(), new FakeCatalogs(), orders);

        var result = await handler.HandleAsync(Stream.Null, default);

        Assert.Equal(ImportOutcome.InvalidFormat, result.Outcome);
        Assert.Equal("bad file", result.Message);
        Assert.Empty(orders.Saved);
    }

    [Fact]
    public async Task Validation_errors_prevent_saving()
    {
        var orders = new InMemoryOrders();
        var template = new OrderTemplate(Header, [Line("SKU-1", new LocationQuantity("ST-9", null, 1m))], []);

        var result = await Handler(() => template, orders).HandleAsync(Stream.Null, default);

        Assert.Equal(ImportOutcome.ValidationFailed, result.Outcome);
        Assert.Contains(result.Issues, i => i.Code == "UNKNOWN_LOCATION");
        Assert.Empty(orders.Saved);
    }

    [Fact]
    public async Task Existing_order_code_is_rejected()
    {
        var orders = new InMemoryOrders();
        var handler = Handler(ValidTemplate, orders);
        await handler.HandleAsync(Stream.Null, default);

        var second = await handler.HandleAsync(Stream.Null, default);

        Assert.Equal(ImportOutcome.DuplicateOrderCode, second.Outcome);
        Assert.Equal("DUPLICATE_ORDER_CODE", Assert.Single(second.Issues).Code);
        Assert.Single(orders.Saved);
    }

    [Fact]
    public async Task Excel_file_is_imported_end_to_end_with_the_real_reader()
    {
        using var workbook = new XLWorkbook();
        var header = workbook.AddWorksheet("cabecera");
        header.Cell("A1").Value = "CAMPO";
        header.Cell("A2").Value = "CODIGO PEDIDO";
        header.Cell("B2").Value = "PO-E2E";
        header.Cell("A3").Value = "FECHA DE PEDIDO";
        header.Cell("B3").Value = new DateTime(2026, 9, 24);
        header.Cell("A4").Value = "NOMBRE PROVEEDOR";
        header.Cell("B4").Value = "ACME";
        var detail = workbook.AddWorksheet("detalle");
        detail.Cell("A1").Value = "Codigo Producto";
        detail.Cell("B1").Value = "Pack";
        detail.Cell("C1").Value = "Bodega";
        detail.Cell("D1").Value = "Tienda";
        detail.Cell("C2").Value = "WH-1";
        detail.Cell("D2").Value = "ST-1";
        detail.Cell("A3").Value = "SKU-1";
        detail.Cell("B3").Value = "X2";
        detail.Cell("C3").Value = 3;
        detail.Cell("D3").Value = 1.5;
        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        stream.Position = 0;
        var orders = new InMemoryOrders();
        var handler = new ImportOrderHandler(
            new ClosedXmlOrderTemplateReader(), FixedTimeProvider.Validator(), new FakeCatalogs(), orders);

        var result = await handler.HandleAsync(stream, default);

        Assert.Equal(ImportOutcome.Imported, result.Outcome);
        Assert.Equal(4.5m, Assert.Single(Assert.Single(orders.Saved).Lines).TotalQuantity);
    }
}
