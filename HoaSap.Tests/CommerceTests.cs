using System.Net;
using System.Text.Json;
using Xunit;

namespace HoaSap.Tests;

[Collection("api")]
public sealed class CommerceTests(ApiFixture fixture) : ApiTestBase(fixture)
{
    [Fact]
    public async Task GhnDirectoryRequiresAuthenticationAndQuoteRejectsWardOutsideSelectedProvince()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client.GetAsync("/api/shipping/locations/provinces")).StatusCode);
        using var customer = await AuthenticatedAsync(await RegisterAsync($"shipping-catalog-{Guid.NewGuid():N}@example.test"));
        using var provinces = JsonDocument.Parse(await (await customer.GetAsync("/api/shipping/locations/provinces")).Content.ReadAsStringAsync());
        Assert.Equal("Hồ Chí Minh", provinces.RootElement[0].GetProperty("name").GetString());
        using var wards = JsonDocument.Parse(await (await customer.GetAsync("/api/shipping/locations/wards?provinceId=1000001")).Content.ReadAsStringAsync());
        Assert.Equal("Phường Thủ Đức", wards.RootElement[0].GetProperty("name").GetString());

        var invalidQuote = await customer.PostAsync("/api/shipping/quote", Json(new
        {
            streetAddress = "QA street", provinceId = 1000001, wardId = 1003581,
            items = Array.Empty<object>()
        }));
        Assert.Equal(HttpStatusCode.BadRequest, invalidQuote.StatusCode);
    }

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
    public async Task CheckoutWithoutShippingQuote_FailsClosedAndDoesNotReserveStock()
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
        Assert.Equal(HttpStatusCode.ServiceUnavailable, order.StatusCode);
        using var detail = JsonDocument.Parse(await (await Client.GetAsync($"/api/catalog/products/{productId}")).Content.ReadAsStringAsync());
        Assert.Equal(3, detail.RootElement.GetProperty("quantityAvailable").GetInt32());
    }

    [Fact]
    public async Task CheckoutWithoutQuoteCannotBeUsedToBypassShippingReadinessOrReserveStock()
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
        Assert.Equal(HttpStatusCode.ServiceUnavailable, rejected.StatusCode);
        var succeedsAfterFailedAttempt = await customer.PostAsync("/api/orders/checkout", Json(new { shippingAddress = "QA", items = new[] { new { productId, quantity = 1 } } }));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, succeedsAfterFailedAttempt.StatusCode);
        using var detail = JsonDocument.Parse(await (await Client.GetAsync($"/api/catalog/products/{productId}")).Content.ReadAsStringAsync());
        Assert.Equal(1, detail.RootElement.GetProperty("quantityAvailable").GetInt32());
    }
}
