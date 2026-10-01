using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Stockline.Application.Erp;
using Stockline.Infrastructure.Erp;

namespace Stockline.UnitTests.Erp;

public partial class ServiceLayerErpGatewayTests
{
    private sealed partial class FakeServiceLayer : HttpMessageHandler
    {
        private readonly HashSet<string> _sessions = [];
        private int _nextDoc = 1;

        public int Logins { get; private set; }

        public List<(string Path, JsonObject? Body)> Posts { get; } = [];

        public Dictionary<string, List<JsonObject>> Documents { get; } = new() { ["PurchaseOrders"] = [], ["InventoryTransferRequests"] = [] };

        public List<JsonObject> Partners { get; } = [new() { ["CardCode"] = "V-100", ["CardName"] = "ACME SUPPLIER" }];

        public Func<HttpRequestMessage, HttpResponseMessage?>? Intercept { get; set; }

        public void ExpireSessions() => _sessions.Clear();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath.Replace("/b1s/v1/", string.Empty);
            if (path == "Login")
            {
                var credentials = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!.AsObject();
                if (credentials["CompanyDB"]?.GetValue<string>() != "DEMO" || credentials["UserName"]?.GetValue<string>() != "manager"
                    || credentials["Password"] is null)
                {
                    return Json(HttpStatusCode.Unauthorized, Error(-304, "Fail to get DB Credentials from SLD"));
                }

                Logins++;
                var id = $"session-{Logins}";
                _sessions.Add(id);
                return Json(HttpStatusCode.OK, new JsonObject { ["SessionId"] = id });
            }

            var cookie = request.Headers.TryGetValues("Cookie", out var values) ? values.First() : string.Empty;
            if (!_sessions.Contains(cookie.Replace("B1SESSION=", string.Empty)))
            {
                return Json(HttpStatusCode.Unauthorized, Error(301, "Invalid session or session already timeout."));
            }

            if (Intercept?.Invoke(request) is { } custom)
            {
                return custom;
            }

            var query = Uri.UnescapeDataString(request.RequestUri.Query);
            if (request.Method == HttpMethod.Get)
            {
                return Json(HttpStatusCode.OK, new JsonObject { ["value"] = new JsonArray([.. Find(path, query)]) });
            }

            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!.AsObject();
            Posts.Add((path, body));
            var doc = body.DeepClone().AsObject();
            doc["DocEntry"] = _nextDoc;
            doc["DocNum"] = 5000 + _nextDoc++;
            Documents[path].Add(doc);
            return Json(HttpStatusCode.Created, doc);
        }

        private IEnumerable<JsonNode> Find(string path, string query)
        {
            if (path == "BusinessPartners")
            {
                var name = NameFilter().Match(query).Groups[1].Value;
                return Partners.Where(p => p["CardName"]!.GetValue<string>() == name).Select(p => p.DeepClone());
            }

            var key = KeyFilter().Match(query).Groups[1].Value;
            return Documents[path].Where(d => d["U_sap_id"]?.GetValue<string>() == key)
                .Select(d => (JsonNode)new JsonObject { ["DocEntry"] = d["DocEntry"]!.DeepClone(), ["DocNum"] = d["DocNum"]!.DeepClone() });
        }

        public static JsonObject Error(int code, string message) =>
            new() { ["error"] = new JsonObject { ["code"] = code, ["message"] = new JsonObject { ["lang"] = "en-us", ["value"] = message } } };

        public static HttpResponseMessage Json(HttpStatusCode status, JsonNode body) =>
            new(status) { Content = JsonContent.Create(body) };

        [GeneratedRegex(@"CardName eq '([^']*)'")]
        private static partial Regex NameFilter();

        [GeneratedRegex(@"U_sap_id eq '([^']*)'")]
        private static partial Regex KeyFilter();
    }

    private static (ServiceLayerErpGateway Gateway, FakeServiceLayer Server) Create()
    {
        var server = new FakeServiceLayer();
        var http = new HttpClient(server) { BaseAddress = new Uri("http://erp/b1s/v1/") };
        return (new ServiceLayerErpGateway(http, Options.Create(new ErpOptions()), new ErpSession()), server);
    }

    private static PurchaseOrderRequest PurchaseOrder(string key = "key-1", string supplier = "ACME SUPPLIER") => new(
        key, supplier, "WH-A", new DateOnly(2026, 10, 1), new DateOnly(2026, 11, 1), "Order PO-1",
        [new ErpLine("SKU-1", 6m, 2.8m, 20m)]);

    private static TransferRequestRequest Transfer(string key = "tr-1") => new(
        key, "WH-A", "ST-A1", new DateOnly(2026, 10, 1), [new ErpLine("SKU-1", 1m, 0m, 0m)]);

    [Fact]
    public async Task Purchase_order_is_created_with_the_resolved_supplier_and_idempotency_key()
    {
        var (gateway, server) = Create();

        var result = await gateway.CreatePurchaseOrderAsync(PurchaseOrder(), default);

        Assert.True(result.Success);
        Assert.Equal((1L, 5001L), (result.DocEntry, result.DocNum));
        var body = Assert.Single(server.Posts).Body!;
        Assert.Equal("V-100", body["CardCode"]!.GetValue<string>());
        Assert.Equal("key-1", body["U_sap_id"]!.GetValue<string>());
        Assert.Equal("2026-10-01", body["DocDate"]!.GetValue<string>());
        Assert.Equal("2026-11-01", body["DocDueDate"]!.GetValue<string>());
        var line = body["DocumentLines"]![0]!;
        Assert.Equal(("SKU-1", 6m, "WH-A", 2.8m, 20m), (
            line["ItemCode"]!.GetValue<string>(), line["Quantity"]!.GetValue<decimal>(), line["WarehouseCode"]!.GetValue<string>(),
            line["UnitPrice"]!.GetValue<decimal>(), line["DiscountPercent"]!.GetValue<decimal>()));
    }

    [Fact]
    public async Task Repeating_a_request_returns_the_existing_document_instead_of_creating_another()
    {
        var (gateway, server) = Create();
        var first = await gateway.CreatePurchaseOrderAsync(PurchaseOrder(), default);

        var second = await gateway.CreatePurchaseOrderAsync(PurchaseOrder(), default);

        Assert.Equal((first.DocEntry, first.DocNum), (second.DocEntry, second.DocNum));
        Assert.Single(server.Posts);
    }

    [Fact]
    public async Task Transfer_request_uses_the_from_and_to_warehouses_on_header_and_lines()
    {
        var (gateway, server) = Create();

        var result = await gateway.CreateTransferRequestAsync(Transfer(), default);

        Assert.True(result.Success);
        var (path, body) = Assert.Single(server.Posts);
        Assert.Equal("InventoryTransferRequests", path);
        Assert.Equal(("WH-A", "ST-A1"), (body!["FromWarehouse"]!.GetValue<string>(), body["ToWarehouse"]!.GetValue<string>()));
        var line = body["StockTransferLines"]![0]!;
        Assert.Equal(("WH-A", "ST-A1"), (line["FromWarehouseCode"]!.GetValue<string>(), line["WarehouseCode"]!.GetValue<string>()));
    }

    [Fact]
    public async Task One_login_serves_many_requests_and_an_expired_session_is_renewed_transparently()
    {
        var (gateway, server) = Create();
        await gateway.CreatePurchaseOrderAsync(PurchaseOrder("a"), default);
        await gateway.CreatePurchaseOrderAsync(PurchaseOrder("b"), default);
        Assert.Equal(1, server.Logins);

        server.ExpireSessions();
        var result = await gateway.CreatePurchaseOrderAsync(PurchaseOrder("c"), default);

        Assert.True(result.Success);
        Assert.Equal(2, server.Logins);
    }

    [Fact]
    public async Task Unknown_supplier_is_a_permanent_failure_without_creating_anything()
    {
        var (gateway, server) = Create();

        var result = await gateway.CreatePurchaseOrderAsync(PurchaseOrder(supplier: "NOBODY"), default);

        Assert.False(result.Success);
        Assert.False(result.IsTransient);
        Assert.Contains("NOBODY", result.Error);
        Assert.Empty(server.Posts);
    }

    [Fact]
    public async Task Erp_validation_errors_are_permanent_and_carry_the_erp_message()
    {
        var (gateway, server) = Create();
        server.Intercept = r => r.Method == HttpMethod.Post
            ? FakeServiceLayer.Json(HttpStatusCode.BadRequest, FakeServiceLayer.Error(-10, "Item 'SKU-1' does not exist"))
            : null;

        var result = await gateway.CreatePurchaseOrderAsync(PurchaseOrder(), default);

        Assert.Equal((false, false, "Item 'SKU-1' does not exist"), (result.Success, result.IsTransient, result.Error));
    }

    [Fact]
    public async Task Server_errors_are_transient()
    {
        var (gateway, server) = Create();
        server.Intercept = r => r.Method == HttpMethod.Post ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : null;

        var result = await gateway.CreateTransferRequestAsync(Transfer(), default);

        Assert.Equal((false, true), (result.Success, result.IsTransient));
        Assert.Contains("503", result.Error);
    }

    [Fact]
    public async Task Unreachable_erp_is_a_transient_failure_not_an_exception()
    {
        var (gateway, server) = Create();
        server.Intercept = _ => throw new HttpRequestException("connection refused");

        var result = await gateway.CreatePurchaseOrderAsync(PurchaseOrder(), default);

        Assert.Equal((false, true), (result.Success, result.IsTransient));
        Assert.Contains("connection refused", result.Error);
    }

    private static HttpResponseMessage? Lookup(HttpRequestMessage request, string[] items, string[] warehouses)
    {
        var path = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath).Replace("/b1s/v1/", string.Empty);
        var match = Regex.Match(path, @"^(Items|Warehouses)\('(.*)'\)$");
        if (!match.Success)
        {
            return null;
        }

        var known = match.Groups[1].Value == "Items" ? items : warehouses;
        return known.Contains(match.Groups[2].Value)
            ? FakeServiceLayer.Json(HttpStatusCode.OK, new JsonObject { ["ItemCode"] = match.Groups[2].Value })
            : FakeServiceLayer.Json(HttpStatusCode.NotFound, FakeServiceLayer.Error(-2028, "Unknown object type"));
    }

    private static MasterDataCheck Check(params string[] items) => new("ACME SUPPLIER", items, ["WH-A"]);

    [Fact]
    public async Task Master_data_check_passes_when_supplier_items_and_warehouses_exist()
    {
        var (gateway, server) = Create();
        server.Intercept = r => Lookup(r, ["SKU-1", "SKU-2"], ["WH-A"]);

        var result = await gateway.CheckMasterDataAsync(Check("SKU-1", "SKU-2"), default);

        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task Master_data_check_lists_every_missing_master()
    {
        var (gateway, server) = Create();
        server.Intercept = r => Lookup(r, ["SKU-1"], []);

        var result = await gateway.CheckMasterDataAsync(new MasterDataCheck("UNKNOWN CO", ["SKU-1", "SKU-9"], ["WH-A"]), default);

        Assert.False(result.IsValid);
        Assert.False(result.IsTransient);
        Assert.Equal(
            ["supplier 'UNKNOWN CO' not found", "items not found: SKU-9", "warehouses not found: WH-A"],
            result.Problems);
        Assert.StartsWith("ERP master data check failed: supplier", result.Describe());
    }

    [Fact]
    public async Task Master_data_check_reports_server_errors_as_transient_and_not_as_missing_data()
    {
        var (gateway, server) = Create();
        server.Intercept = r => r.RequestUri!.AbsolutePath.Contains("Items")
            ? FakeServiceLayer.Json(HttpStatusCode.InternalServerError, FakeServiceLayer.Error(-1, "boom"))
            : Lookup(r, [], ["WH-A"]);

        var result = await gateway.CheckMasterDataAsync(Check("SKU-1"), default);

        Assert.False(result.IsValid);
        Assert.True(result.IsTransient);
        Assert.Empty(result.Problems);
        Assert.Contains("500", result.Error);
    }

    [Fact]
    public async Task Master_data_check_treats_an_unreachable_erp_as_transient()
    {
        var (gateway, server) = Create();
        server.Intercept = _ => throw new HttpRequestException("connection refused");

        var result = await gateway.CheckMasterDataAsync(Check("SKU-1"), default);

        Assert.True(result.IsTransient);
        Assert.Contains("unreachable", result.Error);
    }

    private static JsonObject Item(string code, string? pack = "X6", string valid = "tYES", string frozen = "tNO", decimal retail = 12.99m) => new()
    {
        ["ItemCode"] = code,
        ["ItemName"] = $"Name of {code}",
        ["BarCode"] = "7869000000013",
        ["U_IXX_PACK"] = pack,
        ["Valid"] = valid,
        ["Frozen"] = frozen,
        ["ItemPrices"] = new JsonArray(
            new JsonObject { ["PriceList"] = 1, ["Price"] = retail },
            new JsonObject { ["PriceList"] = 2, ["Price"] = 5.6m }),
    };

    private static HttpResponseMessage? Catalog(HttpRequestMessage request, params JsonObject[] items)
    {
        var path = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath).Replace("/b1s/v1/", string.Empty);
        var key = Regex.Match(path, @"^Items\('(.*)'\)$");
        if (key.Success)
        {
            var item = items.FirstOrDefault(i => i["ItemCode"]!.GetValue<string>() == key.Groups[1].Value);
            return item is null
                ? FakeServiceLayer.Json(HttpStatusCode.NotFound, FakeServiceLayer.Error(-2028, "Unknown object type"))
                : FakeServiceLayer.Json(HttpStatusCode.OK, item.DeepClone());
        }

        if (path == "Items")
        {
            var prefix = Regex.Match(Uri.UnescapeDataString(request.RequestUri.Query), @"startswith\(ItemCode,'([^']*)'\)").Groups[1].Value;
            var matches = items.Where(i => i["ItemCode"]!.GetValue<string>().StartsWith(prefix) && i["Valid"]!.GetValue<string>() == "tYES");
            return FakeServiceLayer.Json(HttpStatusCode.OK, new JsonObject { ["value"] = new JsonArray([.. matches.Select(m => m.DeepClone())]) });
        }

        return null;
    }

    [Fact]
    public async Task Catalog_lookup_reads_name_barcode_pack_retail_price_and_active_flag()
    {
        var (gateway, server) = Create();
        server.Intercept = r => Catalog(r, Item("SKU-1"), Item("SKU-2", "X3", valid: "tNO"), Item("SKU-3", frozen: "tYES"), Item("SKU-4", pack: null));

        var result = await gateway.GetItemsAsync(["SKU-1", "SKU-2", "SKU-3", "SKU-4", "NOPE"], default);

        Assert.True(result.Succeeded);
        Assert.Equal(["SKU-1", "SKU-2", "SKU-3", "SKU-4"], result.Found.Keys.Order());
        var item = result.Found["SKU-1"];
        Assert.Equal(("Name of SKU-1", "7869000000013", "X6", 12.99m, true), (item.Name, item.Barcode, item.Pack, item.RetailPrice, item.IsActive));
        Assert.False(result.Found["SKU-2"].IsActive);
        Assert.False(result.Found["SKU-3"].IsActive);
        Assert.Null(result.Found["SKU-4"].Pack);
    }

    [Fact]
    public async Task Catalog_lookup_reports_server_errors_and_unreachable_erp_as_errors_not_as_missing()
    {
        var (gateway, server) = Create();
        server.Intercept = r => FakeServiceLayer.Json(HttpStatusCode.InternalServerError, FakeServiceLayer.Error(-1, "boom"));
        var serverError = await gateway.GetItemsAsync(["SKU-1"], default);

        var (down, downServer) = Create();
        downServer.Intercept = _ => throw new HttpRequestException("connection refused");
        var unreachable = await down.GetItemsAsync(["SKU-1"], default);

        Assert.False(serverError.Succeeded);
        Assert.Contains("500", serverError.Error);
        Assert.False(unreachable.Succeeded);
        Assert.Contains("unreachable", unreachable.Error);
    }

    [Fact]
    public async Task Suggestions_shorten_the_code_until_active_products_match()
    {
        var (gateway, server) = Create();
        server.Intercept = r => Catalog(r, Item("SHIRT-RED"), Item("SHIRT-BLUE"), Item("SHIRT-OLD", valid: "tNO"), Item("PANTS-1"));

        var suggestions = await gateway.SuggestItemsAsync("SHIRT-GREEN", 5, default);

        Assert.Equal(["SHIRT-RED", "SHIRT-BLUE"], suggestions.Select(s => s.Code));
        Assert.Empty(await gateway.SuggestItemsAsync("ZZZZZZ", 5, default));
    }

    [Fact]
    public async Task Suggestions_never_fail_when_the_erp_is_down()
    {
        var (gateway, server) = Create();
        server.Intercept = _ => throw new HttpRequestException("connection refused");

        Assert.Empty(await gateway.SuggestItemsAsync("SHIRT-GREEN", 5, default));
    }
}
