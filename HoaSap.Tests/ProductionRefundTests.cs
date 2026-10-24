using System.Net;
using System.Text.Json;
using Xunit;

namespace HoaSap.Tests;

[Collection("api")]
public sealed class ProductionRefundTests(ApiFixture fixture) : ApiTestBase(fixture)
{
    [Fact]
    public async Task OrderRefund_EndToEnd_IsProcessableAndSupportsSecondPartialRequest()
    {
        var admin = await AuthenticatedAsync(await AdminTokenAsync());
        var category = await admin.PostAsync("/api/management/categories", Json(new { code = "RF-" + Guid.NewGuid().ToString("N")[..8], name = "Refund QA" }));
        category.EnsureSuccessStatusCode();
        using var categoryJson = JsonDocument.Parse(await category.Content.ReadAsStringAsync());
        var categoryId = categoryJson.RootElement.GetProperty("id").GetGuid();
        var product = await admin.PostAsync("/api/management/products", Json(new { code = "RFP-" + Guid.NewGuid().ToString("N")[..8], name = "Refund Flower", categoryId, price = 100000m, isActive = true }));
        product.EnsureSuccessStatusCode();
        using var productJson = JsonDocument.Parse(await product.Content.ReadAsStringAsync());
        var productId = productJson.RootElement.GetProperty("id").GetGuid();
        (await admin.PutAsync($"/api/management/inventory/{productId}?quantity=1", Json(new { }))).EnsureSuccessStatusCode();

        using var customer = await AuthenticatedAsync(await RegisterAsync($"refund-e2e-{Guid.NewGuid():N}@example.test"));
        var quote = await ShippingQuoteAsync(customer, "QA", (productId, 1));
        var created = await customer.PostAsync("/api/orders/checkout", Json(new { shippingAddress = quote.Destination, shippingQuoteId = quote.Id, items = new[] { new { productId, quantity = 1 } } }));
        created.EnsureSuccessStatusCode();
        using var orderJson = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var orderId = orderJson.RootElement.GetProperty("id").GetGuid();
        (await admin.PostAsync($"/api/payments/order/{orderId}", Json(new { transactionCode = "RF-PAY-" + Guid.NewGuid().ToString("N"), paymentMethod = "Cash", amount = 142000m }))).EnsureSuccessStatusCode();

        var first = await admin.PostAsync($"/api/refunds/order/{orderId}", Json(new { amount = 40000m, reason = "Partial QA" }));
        first.EnsureSuccessStatusCode();
        using var firstJson = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
        var firstId = firstJson.RootElement.GetProperty("id").GetGuid();
        (await admin.PostAsync($"/api/refunds/{firstId}/approve", Json(new { }))).EnsureSuccessStatusCode();
        var processed = await admin.PostAsync($"/api/refunds/{firstId}/process", Json(new { transactionCode = "RF-REF-1" }));
        processed.EnsureSuccessStatusCode();
        var replay = await admin.PostAsync($"/api/refunds/{firstId}/process", Json(new { transactionCode = "RF-REF-1" }));
        replay.EnsureSuccessStatusCode();

        var second = await admin.PostAsync($"/api/refunds/order/{orderId}", Json(new { amount = 102000m, reason = "Remaining QA" }));
        second.EnsureSuccessStatusCode();
        using var secondJson = JsonDocument.Parse(await second.Content.ReadAsStringAsync());
        var secondId = secondJson.RootElement.GetProperty("id").GetGuid();
        (await admin.PostAsync($"/api/refunds/{secondId}/approve", Json(new { }))).EnsureSuccessStatusCode();
        var secondProcessed = await admin.PostAsync($"/api/refunds/{secondId}/process", Json(new { transactionCode = "RF-REF-2" }));
        secondProcessed.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task RefundList_RequiresStaffAuthorization()
    {
        var response = await Client.GetAsync("/api/refunds?page=1&pageSize=200");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task RefundList_IsBoundedAndDoesNotExposePaymentSecrets()
    {
        var admin = await AuthenticatedAsync(await AdminTokenAsync());
        var response = await admin.GetAsync("/api/refunds?page=1&pageSize=1000");
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(1, json.RootElement.GetProperty("page").GetInt32());
        Assert.Equal(100, json.RootElement.GetProperty("pageSize").GetInt32());
        Assert.False(json.RootElement.ToString().Contains("PaymentGatewayResponse", StringComparison.OrdinalIgnoreCase));
        Assert.False(json.RootElement.ToString().Contains("PasswordHash", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RefundDecisions_AreAdminOnly()
    {
        var staffToken = await RegisterAsync($"refund-staff-{Guid.NewGuid():N}@example.test", "096" + Random.Shared.Next(1000000, 9999999));
        var staff = await AuthenticatedAsync(staffToken);
        var response = await staff.PostAsync($"/api/refunds/{Guid.NewGuid()}/approve", Json(new { }));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
