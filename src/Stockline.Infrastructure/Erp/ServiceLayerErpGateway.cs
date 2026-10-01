using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Stockline.Application.Erp;

namespace Stockline.Infrastructure.Erp;

/// <summary>Session shared by every gateway instance so one login serves many requests.</summary>
public sealed class ErpSession
{
    public SemaphoreSlim Lock { get; } = new(1, 1);

    public string? SessionId { get; set; }

    public ConcurrentDictionary<string, string> SupplierCodes { get; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class ServiceLayerErpGateway(HttpClient http, IOptions<ErpOptions> options, ErpSession session) : IErpGateway
{
    private const string IdempotencyField = "U_sap_id";

    private readonly ErpOptions _options = options.Value;

    public async Task<ErpResult> CreatePurchaseOrderAsync(PurchaseOrderRequest request, CancellationToken ct) =>
        await GuardAsync(async () =>
        {
            if (await FindExistingAsync("PurchaseOrders", request.IdempotencyKey, ct) is { } existing)
            {
                return existing;
            }

            var cardCode = await ResolveSupplierAsync(request.SupplierName, ct);
            if (cardCode is null)
            {
                return ErpResult.Failure($"Supplier '{request.SupplierName}' was not found in the ERP.", isTransient: false);
            }

            var body = new JsonObject
            {
                ["CardCode"] = cardCode,
                ["DocDate"] = Date(request.DocDate),
                ["DocDueDate"] = request.DueDate is { } due ? Date(due) : null,
                ["Comments"] = request.Comments,
                [IdempotencyField] = request.IdempotencyKey,
                ["DocumentLines"] = new JsonArray([.. request.Lines.Select(l => (JsonNode)new JsonObject
                {
                    ["ItemCode"] = l.ProductCode,
                    ["Quantity"] = l.Quantity,
                    ["WarehouseCode"] = request.WarehouseCode,
                    ["UnitPrice"] = l.UnitPrice,
                    ["DiscountPercent"] = l.DiscountPercent,
                })]),
            };
            return await CreateAsync("PurchaseOrders", body, ct);
        }, ct);

    public async Task<ErpResult> CreateTransferRequestAsync(TransferRequestRequest request, CancellationToken ct) =>
        await GuardAsync(async () =>
        {
            if (await FindExistingAsync("InventoryTransferRequests", request.IdempotencyKey, ct) is { } existing)
            {
                return existing;
            }

            var body = new JsonObject
            {
                ["DocDate"] = Date(request.DocDate),
                ["FromWarehouse"] = request.FromWarehouse,
                ["ToWarehouse"] = request.ToWarehouse,
                [IdempotencyField] = request.IdempotencyKey,
                ["StockTransferLines"] = new JsonArray([.. request.Lines.Select(l => (JsonNode)new JsonObject
                {
                    ["ItemCode"] = l.ProductCode,
                    ["Quantity"] = l.Quantity,
                    ["FromWarehouseCode"] = request.FromWarehouse,
                    ["WarehouseCode"] = request.ToWarehouse,
                })]),
            };
            return await CreateAsync("InventoryTransferRequests", body, ct);
        }, ct);

    public async Task<MasterDataResult> CheckMasterDataAsync(MasterDataCheck check, CancellationToken ct)
    {
        try
        {
            var problems = new List<string>();
            if (await ResolveSupplierAsync(check.SupplierName, ct) is null)
            {
                problems.Add($"supplier '{check.SupplierName}' not found");
            }

            var missingItems = await MissingAsync("Items", check.ItemCodes, ct);
            if (missingItems.Count > 0)
            {
                problems.Add($"items not found: {string.Join(", ", missingItems)}");
            }

            var missingWarehouses = await MissingAsync("Warehouses", check.WarehouseCodes, ct);
            if (missingWarehouses.Count > 0)
            {
                problems.Add($"warehouses not found: {string.Join(", ", missingWarehouses)}");
            }

            return problems.Count == 0 ? MasterDataResult.Ok : new MasterDataResult(problems);
        }
        catch (HttpRequestException ex)
        {
            return new MasterDataResult([], $"The ERP is unreachable: {ex.Message}", IsTransient: true);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return new MasterDataResult([], "The ERP did not answer in time.", IsTransient: true);
        }
        catch (ErpUnavailableException ex)
        {
            return new MasterDataResult([], ex.Message, IsTransient: true);
        }
    }

    private async Task<List<string>> MissingAsync(string collection, IEnumerable<string> codes, CancellationToken ct)
    {
        var missing = new List<string>();
        foreach (var code in codes.Order(StringComparer.Ordinal))
        {
            using var response = await SendAsync(HttpMethod.Get, $"{collection}('{Uri.EscapeDataString(code.Replace("'", "''"))}')", null, ct);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                missing.Add(code);
            }
            else if (!response.IsSuccessStatusCode)
            {
                throw new ErpUnavailableException($"The ERP answered {(int)response.StatusCode} while looking up {collection}('{code}').");
            }
        }

        return missing;
    }

    private sealed class ErpUnavailableException(string message) : Exception(message);

    private static string Date(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static async Task<ErpResult> GuardAsync(Func<Task<ErpResult>> action, CancellationToken ct)
    {
        try
        {
            return await action();
        }
        catch (HttpRequestException ex)
        {
            return ErpResult.Failure($"The ERP is unreachable: {ex.Message}", isTransient: true);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return ErpResult.Failure("The ERP did not answer in time.", isTransient: true);
        }
    }

    private async Task<ErpResult?> FindExistingAsync(string collection, string key, CancellationToken ct)
    {
        var filter = Uri.EscapeDataString($"{IdempotencyField} eq '{key.Replace("'", "''")}'");
        using var response = await SendAsync(HttpMethod.Get, $"{collection}?$filter={filter}&$select=DocEntry,DocNum", null, ct);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var found = (await response.Content.ReadFromJsonAsync<JsonObject>(ct))?["value"]?.AsArray().FirstOrDefault();
        return found is null ? null : ErpResult.Ok(found["DocEntry"]!.GetValue<long>(), found["DocNum"]!.GetValue<long>());
    }

    private async Task<string?> ResolveSupplierAsync(string name, CancellationToken ct)
    {
        if (session.SupplierCodes.TryGetValue(name, out var cached))
        {
            return cached;
        }

        var filter = Uri.EscapeDataString($"CardName eq '{name.Replace("'", "''")}'");
        using var response = await SendAsync(HttpMethod.Get, $"BusinessPartners?$filter={filter}&$select=CardCode", null, ct);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var code = (await response.Content.ReadFromJsonAsync<JsonObject>(ct))?["value"]?.AsArray().FirstOrDefault()?["CardCode"]?.GetValue<string>();
        if (code is not null)
        {
            session.SupplierCodes[name] = code;
        }

        return code;
    }

    private async Task<ErpResult> CreateAsync(string collection, JsonObject body, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Post, collection, body, ct);
        if (response.IsSuccessStatusCode)
        {
            var created = await response.Content.ReadFromJsonAsync<JsonObject>(ct);
            return ErpResult.Ok(created!["DocEntry"]!.GetValue<long>(), created["DocNum"]!.GetValue<long>());
        }

        var transient = (int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.RequestTimeout;
        return ErpResult.Failure(await ReadErrorAsync(response, ct), transient);
    }

    private static async Task<string> ReadErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var error = await response.Content.ReadFromJsonAsync<JsonObject>(ct);
            var message = error?["error"]?["message"]?["value"]?.GetValue<string>();
            if (message is not null)
            {
                return message;
            }
        }
        catch (JsonException)
        {
        }

        return $"The ERP answered {(int)response.StatusCode} {response.ReasonPhrase}.";
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, JsonNode? body, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var sessionId = await EnsureSessionAsync(ct);
            using var request = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body) };
            request.Headers.Add("Cookie", $"B1SESSION={sessionId}");
            var response = await http.SendAsync(request, ct);
            if (response.StatusCode != HttpStatusCode.Unauthorized || attempt > 0)
            {
                return response;
            }

            response.Dispose();
            await InvalidateSessionAsync(sessionId, ct);
        }
    }

    private async Task<string> EnsureSessionAsync(CancellationToken ct)
    {
        await session.Lock.WaitAsync(ct);
        try
        {
            if (session.SessionId is { } existing)
            {
                return existing;
            }

            // JsonObject keeps the exact property names; the Service Layer is case-sensitive.
            var credentials = new JsonObject
            {
                ["CompanyDB"] = _options.CompanyDb,
                ["UserName"] = _options.UserName,
                ["Password"] = _options.Password,
            };
            using var response = await http.PostAsJsonAsync("Login", credentials, ct);
            response.EnsureSuccessStatusCode();
            var login = await response.Content.ReadFromJsonAsync<JsonObject>(ct);
            session.SessionId = login!["SessionId"]!.GetValue<string>();
            return session.SessionId;
        }
        finally
        {
            session.Lock.Release();
        }
    }

    private async Task InvalidateSessionAsync(string expired, CancellationToken ct)
    {
        await session.Lock.WaitAsync(ct);
        try
        {
            if (session.SessionId == expired)
            {
                session.SessionId = null;
            }
        }
        finally
        {
            session.Lock.Release();
        }
    }
}
