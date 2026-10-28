using System.Net;
using System.Text.Json;
using Xunit;

namespace HoaSap.Tests;

[Collection("api")]
public sealed class CmsCatalogTests(ApiFixture fixture) : ApiTestBase(fixture)
{
    [Fact]
    public async Task CatalogManagement_IsAdminOnly()
    {
        var customer = await AuthenticatedAsync(await RegisterAsync($"catalog-{Guid.NewGuid():N}@example.test"));
        Assert.Equal(HttpStatusCode.Forbidden, (await customer.GetAsync("/api/management/categories")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client.GetAsync("/api/management/products")).StatusCode);
    }

    [Fact]
    public async Task ProductPaging_RejectsInvalidValues()
    {
        var admin = await AuthenticatedAsync(await AdminTokenAsync());
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync("/api/management/products?page=0&pageSize=20")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync("/api/management/products?page=1&pageSize=101")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync("/api/management/products?page=2147483647&pageSize=100")).StatusCode);
    }

    [Fact]
    public async Task CategoryCreate_AcceptsManagedMediaUrlAndRejectsUnsafeUrl()
    {
        var admin = await AuthenticatedAsync(await AdminTokenAsync());
        var valid = await admin.PostAsync("/api/management/categories", Json(new { code = $"cat-{Guid.NewGuid():N}"[..20], name = "Ảnh", imageUrl = "/api/media/0123456789abcdef0123456789abcdef.webp" }));
        Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
        using var body = JsonDocument.Parse(await valid.Content.ReadAsStringAsync());
        Assert.Equal("/api/media/0123456789abcdef0123456789abcdef.webp", body.RootElement.GetProperty("imageUrl").GetString());

        var invalid = await admin.PostAsync("/api/management/categories", Json(new { code = $"bad-{Guid.NewGuid():N}"[..20], name = "Bad", imageUrl = "/uploads/file.png" }));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    [Fact]
    public async Task CategoryUpdateDelete_RejectsDuplicatesAndReferencedDelete()
    {
        var admin = await AuthenticatedAsync(await AdminTokenAsync());
        var first = await CreateCategoryAsync(admin, "first");
        var second = await CreateCategoryAsync(admin, "second");

        var duplicate = await admin.PutAsync($"/api/management/categories/{second.Id}", Json(new { code = first.Code, name = "Duplicate" }));
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);

        var product = await CreateProductAsync(admin, first.Id, "referenced");
        var referencedDelete = await admin.DeleteAsync($"/api/management/categories/{first.Id}");
        Assert.Equal(HttpStatusCode.Conflict, referencedDelete.StatusCode);

        var update = await admin.PutAsync($"/api/management/categories/{first.Id}", Json(new { code = first.Code + "-edit", name = "Updated", description = "changed" }));
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/management/products/{product.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/management/categories/{first.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/management/categories/{second.Id}")).StatusCode);
    }

    [Fact]
    public async Task ProductLifecycle_ValidatesDuplicateCodesAndDeletesUnreferencedStock()
    {
        var admin = await AuthenticatedAsync(await AdminTokenAsync());
        var category = await CreateCategoryAsync(admin, "product");
        var product = await CreateProductAsync(admin, category.Id, "editable");

        var duplicate = await admin.PostAsync("/api/management/products", ProductJson(category.Id, product.Code));
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);

        var edit = await admin.PutAsync($"/api/management/products/{product.Id}", ProductJson(category.Id, product.Code + "-edit", "Edited product", 19.99m));
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
        var stock = await admin.PutAsync($"/api/management/inventory/{product.Id}?quantity=7", Json(new { }));
        Assert.Equal(HttpStatusCode.NoContent, stock.StatusCode);

        var deleted = await admin.DeleteAsync($"/api/management/products/{product.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/management/products/{product.Id}")).StatusCode);
    }

    [Fact]
    public async Task ProductPackaging_HasNoSyntheticDefaultAndRequiresAllMeasurementsForConfirmation()
    {
        using var admin = await AuthenticatedAsync(await AdminTokenAsync());
        var category = await CreateCategoryAsync(admin, "packaging");
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var draft = await admin.PostAsync("/api/management/products", Json(new { categoryId = category.Id, code = "DRAFT-" + suffix, name = "Unmeasured SKU", price = 100m, isActive = true }));
        Assert.Equal(HttpStatusCode.OK, draft.StatusCode);
        using (var draftJson = JsonDocument.Parse(await draft.Content.ReadAsStringAsync()))
        {
            Assert.Equal(JsonValueKind.Null, draftJson.RootElement.GetProperty("shippingWeightGrams").ValueKind);
            Assert.False(draftJson.RootElement.GetProperty("packagingSpecsConfirmed").GetBoolean());
        }

        var incomplete = await admin.PostAsync("/api/management/products", Json(new { categoryId = category.Id, code = "BAD-" + suffix, name = "Incomplete SKU", price = 100m, shippingWeightGrams = 500, packageLengthCm = 30m, packagingSpecsConfirmed = true, isActive = true }));
        Assert.Equal(HttpStatusCode.BadRequest, incomplete.StatusCode);
        var invalidPrecision = await admin.PostAsync("/api/management/products", Json(new { categoryId = category.Id, code = "SCALE-" + suffix, name = "Invalid scale SKU", price = 100m, shippingWeightGrams = 500, packageLengthCm = 30.001m, packageWidthCm = 20m, packageHeightCm = 10m, packagingSpecsConfirmed = true, isActive = true }));
        Assert.Equal(HttpStatusCode.BadRequest, invalidPrecision.StatusCode);

        var complete = await admin.PostAsync("/api/management/products", Json(new { categoryId = category.Id, code = "READY-" + suffix, name = "Measured SKU", price = 100m, shippingWeightGrams = 500, packageLengthCm = 30m, packageWidthCm = 20m, packageHeightCm = 10m, packagingSpecsConfirmed = true, isActive = true }));
        Assert.Equal(HttpStatusCode.OK, complete.StatusCode);
        using var created = JsonDocument.Parse(await complete.Content.ReadAsStringAsync());
        var productId = created.RootElement.GetProperty("id").GetGuid();
        var detailResponse = await admin.GetAsync($"/api/management/products/{productId}");
        using var detail = JsonDocument.Parse(await detailResponse.Content.ReadAsStringAsync());
        Assert.Equal(500, detail.RootElement.GetProperty("shippingWeightGrams").GetInt32());
        Assert.Equal(30m, detail.RootElement.GetProperty("packageLengthCm").GetDecimal());
        Assert.Equal(20m, detail.RootElement.GetProperty("packageWidthCm").GetDecimal());
        Assert.Equal(10m, detail.RootElement.GetProperty("packageHeightCm").GetDecimal());
        Assert.True(detail.RootElement.GetProperty("packagingSpecsConfirmed").GetBoolean());

        var listResponse = await admin.GetAsync($"/api/management/products?search=READY-{suffix}");
        using var list = JsonDocument.Parse(await listResponse.Content.ReadAsStringAsync());
        Assert.True(list.RootElement.GetProperty("items")[0].GetProperty("packagingSpecsConfirmed").GetBoolean());

        var oldStyleUpdate = await admin.PutAsync($"/api/management/products/{productId}", ProductJson(category.Id, "READY-" + suffix, "Edited without packaging", 101m));
        Assert.Equal(HttpStatusCode.OK, oldStyleUpdate.StatusCode);
        using var afterUpdate = JsonDocument.Parse(await oldStyleUpdate.Content.ReadAsStringAsync());
        Assert.Equal(500, afterUpdate.RootElement.GetProperty("shippingWeightGrams").GetInt32());
        Assert.Equal(30m, afterUpdate.RootElement.GetProperty("packageLengthCm").GetDecimal());
        Assert.True(afterUpdate.RootElement.GetProperty("packagingSpecsConfirmed").GetBoolean());

        var clearSpecs = await admin.PutAsync($"/api/management/products/{productId}", Json(new { categoryId = category.Id, code = "READY-" + suffix, name = "Measurements cleared", price = 102m, shippingWeightGrams = (int?)null, packageLengthCm = (decimal?)null, packageWidthCm = (decimal?)null, packageHeightCm = (decimal?)null, packagingSpecsConfirmed = false, isActive = true }));
        Assert.Equal(HttpStatusCode.OK, clearSpecs.StatusCode);
        using var cleared = JsonDocument.Parse(await clearSpecs.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Null, cleared.RootElement.GetProperty("shippingWeightGrams").ValueKind);
        Assert.False(cleared.RootElement.GetProperty("packagingSpecsConfirmed").GetBoolean());
    }

    [Fact]
    public async Task ProductDelete_RejectsCartReference_AndGallerySupportsAddEditReorderDelete()
    {
        var admin = await AuthenticatedAsync(await AdminTokenAsync());
        var category = await CreateCategoryAsync(admin, "gallery");
        var product = await CreateProductAsync(admin, category.Id, "gallery");
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsync($"/api/management/inventory/{product.Id}?quantity=2", Json(new { }))).StatusCode);

        var customer = await AuthenticatedAsync(await RegisterAsync($"catalog-cart-{Guid.NewGuid():N}@example.test"));
        Assert.Equal(HttpStatusCode.NoContent, (await customer.PostAsync("/api/cart", Json(new { productId = product.Id, quantity = 1 }))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.DeleteAsync($"/api/management/products/{product.Id}")).StatusCode);

        var add = await admin.PostAsync($"/api/management/products/{product.Id}/images", Json(new { imageUrl = "https://cdn.example.test/one.jpg", displayOrder = 0 }));
        Assert.Equal(HttpStatusCode.OK, add.StatusCode);
        var imageId = ReadGuid(add, "id");
        var addSecond = await admin.PostAsync($"/api/management/products/{product.Id}/images", Json(new { imageUrl = "https://cdn.example.test/two.jpg", displayOrder = 1 }));
        Assert.Equal(HttpStatusCode.OK, addSecond.StatusCode);
        var secondId = ReadGuid(addSecond, "id");
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync($"/api/management/products/{product.Id}/images", Json(new { imageUrl = "https://cdn.example.test/dup.jpg", displayOrder = 1 }))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsync($"/api/management/products/{product.Id}/images/{imageId}", Json(new { imageUrl = "https://cdn.example.test/edited.jpg", displayOrder = 2 }))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"/api/management/products/{product.Id}/images")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/management/products/{product.Id}/images/{secondId}")).StatusCode);

        // Remove the cart reference through the customer API, then the product is deletable.
        using var cart = JsonDocument.Parse(await (await customer.GetAsync("/api/cart")).Content.ReadAsStringAsync());
        var cartId = cart.RootElement[0].GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NoContent, (await customer.DeleteAsync($"/api/cart/{cartId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/management/products/{product.Id}")).StatusCode);
    }

    private async Task<(Guid Id, string Code)> CreateCategoryAsync(HttpClient admin, string suffix)
    {
        var code = $"{suffix}-{Guid.NewGuid():N}"[..Math.Min(35, suffix.Length + 33)];
        var response = await admin.PostAsync("/api/management/categories", Json(new { code, name = $"Category {suffix}" }));
        response.EnsureSuccessStatusCode();
        return (ReadGuid(response, "id"), code);
    }

    private async Task<(Guid Id, string Code)> CreateProductAsync(HttpClient admin, Guid categoryId, string suffix)
    {
        var code = $"{suffix}-{Guid.NewGuid():N}"[..Math.Min(70, suffix.Length + 33)];
        var response = await admin.PostAsync("/api/management/products", ProductJson(categoryId, code));
        response.EnsureSuccessStatusCode();
        return (ReadGuid(response, "id"), code);
    }

    private static StringContent ProductJson(Guid categoryId, string code, string name = "Product", decimal price = 10m) =>
        Json(new { categoryId, code, name, price, isActive = true });

    private static Guid ReadGuid(HttpResponseMessage response, string property)
    {
        using var document = JsonDocument.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
        return document.RootElement.GetProperty(property).GetGuid();
    }
}
