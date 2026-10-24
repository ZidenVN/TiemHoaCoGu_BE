using System.Net;
using System.Text.Json;
using Xunit;

namespace HoaSap.Tests;

[Collection("api")]
public sealed class CommerceTests(ApiFixture fixture) : ApiTestBase(fixture)
{
    [Fact]
    public async Task CatalogList_ReportsTheSameAvailableStockAsProductDetail()
    {
        var admin = await AuthenticatedAsync(await AdminTokenAsync());
        var token = Guid.NewGuid().ToString("N");
        var categoryResponse = await admin.PostAsync("/api/management/categories", Json(new { code = "QA-STOCK-CAT-" + token[..8], name = "QA Stock Catalog" }));
        categoryResponse.EnsureSuccessStatusCode();
        using var category = JsonDocument.Parse(await categoryResponse.Content.ReadAsStringAsync());
        var categoryId = category.RootElement.GetProperty("id").GetGuid();

        async Task<Guid> CreateProduct(string suffix)
        {
            var response = await admin.PostAsync("/api/management/products", Json(new
            {
                code = "QA-STOCK-" + token[..12] + "-" + suffix,
                name = "QA Stock " + token,
                categoryId,
                price = 100000m,
                isActive = true
            }));
            response.EnsureSuccessStatusCode();
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return body.RootElement.GetProperty("id").GetGuid();
        }

        var positiveId = await CreateProduct("positive");
        var zeroId = await CreateProduct("zero");
        var missingInventoryId = await CreateProduct("missing");
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsync($"/api/management/inventory/{positiveId}?quantity=7", Json(new { }))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsync($"/api/management/inventory/{zeroId}?quantity=0", Json(new { }))).StatusCode);

        using var list = JsonDocument.Parse(await (await Client.GetAsync($"/api/catalog/products?search={token}&page=1&pageSize=100")).Content.ReadAsStringAsync());
        var items = list.RootElement.GetProperty("items");
        Assert.Equal(3, items.GetArrayLength());

        foreach (var productId in new[] { positiveId, zeroId, missingInventoryId })
        {
            var detailResponse = await Client.GetAsync($"/api/catalog/products/{productId}");
            detailResponse.EnsureSuccessStatusCode();
            using var detail = JsonDocument.Parse(await detailResponse.Content.ReadAsStringAsync());
            var detailQuantity = detail.RootElement.GetProperty("quantityAvailable").GetInt32();
            var listItem = items.EnumerateArray().Single(item => item.GetProperty("id").GetGuid() == productId);
            Assert.Equal(detailQuantity, listItem.GetProperty("quantityAvailable").GetInt32());
        }

        Assert.Equal(7, items.EnumerateArray().Single(item => item.GetProperty("id").GetGuid() == positiveId).GetProperty("quantityAvailable").GetInt32());
        Assert.Equal(0, items.EnumerateArray().Single(item => item.GetProperty("id").GetGuid() == zeroId).GetProperty("quantityAvailable").GetInt32());
        Assert.Equal(0, items.EnumerateArray().Single(item => item.GetProperty("id").GetGuid() == missingInventoryId).GetProperty("quantityAvailable").GetInt32());
    }

    [Fact]
    public async Task Checkout_UsesServerProductPriceAndServerShipping()
    {
        var admin = await AuthenticatedAsync(await AdminTokenAsync());
        var categoryResponse = await admin.PostAsync("/api/management/categories", Json(new { code = "QA-CAT-" + Guid.NewGuid().ToString("N")[..8], name = "QA Flowers" }));
        categoryResponse.EnsureSuccessStatusCode();
        using var category = JsonDocument.Parse(await categoryResponse.Content.ReadAsStringAsync());
        var categoryId = category.RootElement.GetProperty("id").GetGuid();
        var productResponse = await admin.PostAsync("/api/management/products", Json(new { code = "QA-" + Guid.NewGuid().ToString("N")[..8], name = "QA Rose", categoryId, price = 125000m, isActive = true }));
        productResponse.EnsureSuccessStatusCode();
        using var product = JsonDocument.Parse(await productResponse.Content.ReadAsStringAsync());
        var productId = product.RootElement.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsync($"/api/management/inventory/{productId}?quantity=3", Json(new { }))).StatusCode);

        var customer = await AuthenticatedAsync(await RegisterAsync($"checkout-{Guid.NewGuid():N}@example.test", "093" + Random.Shared.Next(1000000, 9999999)));
        var order = await customer.PostAsync("/api/orders/checkout", Json(new { shippingAddress = "QA address", items = new[] { new { productId, quantity = 1, unitPrice = 1m } } }));
        order.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await order.Content.ReadAsStringAsync());
        Assert.Equal(125000m, body.RootElement.GetProperty("subTotal").GetDecimal());
        Assert.Equal(30000m, body.RootElement.GetProperty("shippingFee").GetDecimal());
        Assert.Equal(155000m, body.RootElement.GetProperty("totalAmount").GetDecimal());
        Assert.Equal(125000m, body.RootElement.GetProperty("items")[0].GetProperty("unitPrice").GetDecimal());
    }

    [Fact]
    public async Task Checkout_InsufficientStockDoesNotReserveInventory()
    {
        var admin = await AuthenticatedAsync(await AdminTokenAsync());
        var categoryResponse = await admin.PostAsync("/api/management/categories", Json(new { code = "QA-CAT-" + Guid.NewGuid().ToString("N")[..8], name = "QA Flowers" }));
        categoryResponse.EnsureSuccessStatusCode();
        using var category = JsonDocument.Parse(await categoryResponse.Content.ReadAsStringAsync());
        var categoryId = category.RootElement.GetProperty("id").GetGuid();
        var productResponse = await admin.PostAsync("/api/management/products", Json(new { code = "QA-" + Guid.NewGuid().ToString("N")[..8], name = "QA Tulip", categoryId, price = 50000m, isActive = true }));
        productResponse.EnsureSuccessStatusCode();
        using var product = JsonDocument.Parse(await productResponse.Content.ReadAsStringAsync());
        var productId = product.RootElement.GetProperty("id").GetGuid();
        (await admin.PutAsync($"/api/management/inventory/{productId}?quantity=1", Json(new { }))).EnsureSuccessStatusCode();
        var customer = await AuthenticatedAsync(await RegisterAsync($"stock-{Guid.NewGuid():N}@example.test", "094" + Random.Shared.Next(1000000, 9999999)));
        var line = new[] { new { productId, quantity = 2 } };
        var rejected = await customer.PostAsync("/api/orders/checkout", Json(new { shippingAddress = "QA", items = line }));
        Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
        var succeedsAfterFailedAttempt = await customer.PostAsync("/api/orders/checkout", Json(new { shippingAddress = "QA", items = new[] { new { productId, quantity = 1 } } }));
        Assert.NotEqual(HttpStatusCode.Conflict, succeedsAfterFailedAttempt.StatusCode);
        succeedsAfterFailedAttempt.EnsureSuccessStatusCode();
    }
}
