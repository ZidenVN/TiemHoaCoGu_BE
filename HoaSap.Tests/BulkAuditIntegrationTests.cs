using System.Net;
using System.Text.Json;
using HoaSap.DAL;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace HoaSap.Tests;

[Collection("api")]
public sealed class BulkAuditIntegrationTests(ApiFixture fixture) : ApiTestBase(fixture)
{
    private readonly ApiFixture _fixture = fixture;
    private sealed record AuditRow(string EntityType, string EntityId, string Action, string OldJson, string NewJson, Guid? ActorId, string? ActorRole, string? TraceId);

    private async Task<(Guid ProductId, Guid InventoryId)> ProductAsync(HttpClient admin, int stock)
    {
        var category = await admin.PostAsync("/api/management/categories", Json(new { code = "BA-" + Guid.NewGuid().ToString("N")[..8], name = "Bulk audit" })); category.EnsureSuccessStatusCode();
        using var c = JsonDocument.Parse(await category.Content.ReadAsStringAsync()); var categoryId = c.RootElement.GetProperty("id").GetGuid();
        // Synthetic measurements are limited to test-only fake-carrier scenarios.
        var product = await admin.PostAsync("/api/management/products", Json(new { categoryId, code = "BA-P-" + Guid.NewGuid().ToString("N")[..8], name = "Bulk audit flower", price = 100000m, shippingWeightGrams = 500, packageLengthCm = 30m, packageWidthCm = 20m, packageHeightCm = 10m, packagingSpecsConfirmed = true, isActive = true })); product.EnsureSuccessStatusCode();
        using var p = JsonDocument.Parse(await product.Content.ReadAsStringAsync()); var id = p.RootElement.GetProperty("id").GetGuid();
        (await admin.PutAsync($"/api/management/inventory/{id}?quantity={stock}", Json(new { }))).EnsureSuccessStatusCode();
        await using var scope = _fixture.Services.CreateAsyncScope(); var inventory = await scope.ServiceProvider.GetRequiredService<HoaSapDbContext>().Inventories.SingleAsync(x => x.ProductId == id);
        return (id, inventory.Id);
    }
    private async Task<(Guid Id, string Code)> VoucherAsync(HttpClient admin, int usageLimit = 10)
    {
        var response = await admin.PostAsync("/api/management/vouchers", Json(new { code = "BULK-" + Guid.NewGuid().ToString("N")[..8], description = "audit voucher", discountType = "FixedAmount", discountValue = 10000m, minOrderAmount = 0m, startDate = DateTime.UtcNow.AddMinutes(-5), endDate = DateTime.UtcNow.AddHours(1), usageLimit })); response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); var id = doc.RootElement.GetProperty("id").GetGuid();
        await using var scope = _fixture.Services.CreateAsyncScope(); var code = await scope.ServiceProvider.GetRequiredService<HoaSapDbContext>().Vouchers.Where(x => x.Id == id).Select(x => x.Code).SingleAsync(); return (id, code);
    }
    private async Task<List<AuditRow>> LogsAsync(Guid entityId) { await using var scope = _fixture.Services.CreateAsyncScope(); return await scope.ServiceProvider.GetRequiredService<HoaSapDbContext>().AuditLogs.AsNoTracking().Where(x => x.EntityId == entityId.ToString() && x.Action == "Modified").OrderBy(x => x.OccurredAtUtc).Select(x => new AuditRow(x.EntityType, x.EntityId, x.Action, x.OldValuesJson, x.NewValuesJson, x.ActorId, x.ActorRole, x.TraceId)).ToListAsync(); }
    private static JsonElement Number(string json, string property) { using var d = JsonDocument.Parse(json); var value = d.RootElement.GetProperty(property); Assert.Equal(JsonValueKind.Number, value.ValueKind); return value.Clone(); }
    private static int Int(string json, string property) => Number(json, property).GetInt32();
    private static async Task<Guid> CustomerIdAsync(HttpClient customer) { using var d = JsonDocument.Parse(await (await customer.GetAsync("/api/auth/me")).Content.ReadAsStringAsync()); return d.RootElement.GetProperty("id").GetGuid(); }
    private async Task<(Guid Order, Guid Item)> CheckoutAsync(HttpClient c, Guid product, string? key = null, string? voucher = null)
    { var quote = await ShippingQuoteAsync(c, "QA", (product, 1)); var r = await c.PostAsync("/api/orders/checkout", Json(new { shippingAddress = quote.Destination, shippingQuoteId = quote.Id, idempotencyKey = key, voucherCode = voucher, items = new[] { new { productId = product, quantity = 1 } } })); r.EnsureSuccessStatusCode(); using var d = JsonDocument.Parse(await r.Content.ReadAsStringAsync()); var order = d.RootElement.GetProperty("id").GetGuid(); using var full = JsonDocument.Parse(await (await c.GetAsync($"/api/orders/{order}")).Content.ReadAsStringAsync()); return (order, full.RootElement.GetProperty("items")[0].GetProperty("id").GetGuid()); }

    [Fact]
    public async Task CheckoutWithVoucher_AuditsModifiedRowsWithNumericBeforeAfterActorAndResponseTrace()
    {
        using var admin = await AuthenticatedAsync(await AdminTokenAsync()); var (product, inventory) = await ProductAsync(admin, 2); var (voucher, code) = await VoucherAsync(admin);
        using var customer = await AuthenticatedAsync(await RegisterAsync($"bulk-{Guid.NewGuid():N}@example.test")); var actor = await CustomerIdAsync(customer);
        var quote = await ShippingQuoteAsync(customer, "QA", (product, 1));
        using var response = await customer.PostAsync("/api/orders/checkout", Json(new { shippingAddress = quote.Destination, shippingQuoteId = quote.Id, voucherCode = code, items = new[] { new { productId = product, quantity = 1 } } })); response.EnsureSuccessStatusCode(); var trace = response.Headers.GetValues("X-Request-ID").Single();
        var inventoryLog = (await LogsAsync(inventory)).Single(x => x.EntityType == "Inventory"); var voucherLog = (await LogsAsync(voucher)).Single(x => x.EntityType == "Voucher");
        Assert.Equal(actor, inventoryLog.ActorId); Assert.Equal(actor, voucherLog.ActorId); Assert.Equal("Customer", inventoryLog.ActorRole); Assert.Equal("Customer", voucherLog.ActorRole); Assert.Equal(trace, inventoryLog.TraceId); Assert.Equal(trace, voucherLog.TraceId);
        Assert.Equal(2, Int(inventoryLog.OldJson, "QuantityAvailable")); Assert.Equal(1, Int(inventoryLog.NewJson, "QuantityAvailable")); Assert.Equal(0, Int(inventoryLog.OldJson, "QuantityReserved")); Assert.Equal(1, Int(inventoryLog.NewJson, "QuantityReserved")); Assert.Equal(0, Int(voucherLog.OldJson, "UsedCount")); Assert.Equal(1, Int(voucherLog.NewJson, "UsedCount"));
    }

    [Fact]
    public async Task InvalidVoucherIsBadRequestAndRollbackLeavesNoCustomerModifiedAudit()
    {
        using var admin = await AuthenticatedAsync(await AdminTokenAsync()); var (product, inventory) = await ProductAsync(admin, 1); var (voucher, _) = await VoucherAsync(admin); using var customer = await AuthenticatedAsync(await RegisterAsync($"rollback-{Guid.NewGuid():N}@example.test"));
        var quote = await ShippingQuoteAsync(customer, "QA", (product, 1));
        using var response = await customer.PostAsync("/api/orders/checkout", Json(new { shippingAddress = quote.Destination, shippingQuoteId = quote.Id, voucherCode = "does-not-exist", items = new[] { new { productId = product, quantity = 1 } } })); Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain(await LogsAsync(inventory), x => x.ActorRole == "Customer"); Assert.DoesNotContain(await LogsAsync(voucher), x => x.ActorRole == "Customer");
        await using var scope = _fixture.Services.CreateAsyncScope(); var stock = await scope.ServiceProvider.GetRequiredService<HoaSapDbContext>().Inventories.SingleAsync(x => x.Id == inventory); Assert.Equal(1, stock.QuantityAvailable); Assert.Equal(0, stock.QuantityReserved);
    }

    [Fact]
    public async Task CancellationAndFulfillmentAreAuditedOnceOnReplay()
    {
        using var admin = await AuthenticatedAsync(await AdminTokenAsync()); var (product, inventory) = await ProductAsync(admin, 1); using var customer = await AuthenticatedAsync(await RegisterAsync($"life-{Guid.NewGuid():N}@example.test")); var (order, item) = await CheckoutAsync(customer, product);
        using (var scope = _fixture.Services.CreateScope()) { var o = await scope.ServiceProvider.GetRequiredService<HoaSapDbContext>().Orders.SingleAsync(x => x.Id == order); Assert.Equal(42000m, o.ShippingFee); Assert.Equal(142000m, o.TotalAmount); }
        Assert.Equal(HttpStatusCode.NoContent, (await customer.PostAsync($"/api/orders/{order}/cancel", null)).StatusCode); var cancelledCount = (await LogsAsync(inventory)).Count; Assert.Equal(HttpStatusCode.NoContent, (await customer.PostAsync($"/api/orders/{order}/cancel", null)).StatusCode); Assert.Equal(cancelledCount, (await LogsAsync(inventory)).Count);
        var (order2, item2) = await CheckoutAsync(customer, product); (await admin.PostAsync($"/api/orders/{order2}/status", Json(new { status = "Processing" }))).EnsureSuccessStatusCode(); using var deliveryResponse = await admin.PostAsync($"/api/orders/{order2}/deliveries", Json(new { items = new[] { new { orderItemId = item2, quantity = 1 } } })); deliveryResponse.EnsureSuccessStatusCode(); using var delivery = JsonDocument.Parse(await deliveryResponse.Content.ReadAsStringAsync()); var did = delivery.RootElement.GetProperty("id").GetGuid(); (await admin.PostAsync($"/api/payments/order/{order2}", Json(new { transactionCode = "BA-" + Guid.NewGuid(), paymentMethod = "Cash", amount = 142000m }))).EnsureSuccessStatusCode(); (await admin.PostAsync($"/api/orders/{order2}/status", Json(new { status = "Delivering" }))).EnsureSuccessStatusCode(); foreach (var status in new[] { "Shipped", "Delivered" }) (await admin.PostAsync($"/api/orders/{order2}/deliveries/{did}/status", Json(new { status }))).EnsureSuccessStatusCode(); (await admin.PostAsync($"/api/orders/{order2}/status", Json(new { status = "Completed" }))).EnsureSuccessStatusCode(); var completedCount = (await LogsAsync(inventory)).Count; (await admin.PostAsync($"/api/orders/{order2}/status", Json(new { status = "Completed" }))).EnsureSuccessStatusCode(); Assert.Equal(completedCount, (await LogsAsync(inventory)).Count);
    }

    [Fact]
    public async Task RefundReturnsReservedInventoryAndAddsModifiedAudit()
    {
        using var admin = await AuthenticatedAsync(await AdminTokenAsync()); var (product, inventory) = await ProductAsync(admin, 1); using var customer = await AuthenticatedAsync(await RegisterAsync($"refund-{Guid.NewGuid():N}@example.test")); var (order, _) = await CheckoutAsync(customer, product); (await admin.PostAsync($"/api/payments/order/{order}", Json(new { transactionCode = "BA-RF-" + Guid.NewGuid(), paymentMethod = "Cash", amount = 142000m }))).EnsureSuccessStatusCode(); var request = await admin.PostAsync($"/api/refunds/order/{order}", Json(new { amount = 142000m, reason = "QA return" })); request.EnsureSuccessStatusCode(); await using var scope = _fixture.Services.CreateAsyncScope(); var stock = await scope.ServiceProvider.GetRequiredService<HoaSapDbContext>().Inventories.SingleAsync(x => x.Id == inventory); Assert.Equal(1, stock.QuantityAvailable); Assert.Equal(0, stock.QuantityReserved); Assert.Contains(await LogsAsync(inventory), x => Int(x.NewJson, "QuantityAvailable") == 1 && Int(x.NewJson, "QuantityReserved") == 0);
    }
}
