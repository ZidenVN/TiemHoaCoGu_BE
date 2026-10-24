using System.Net;
using System.Text.Json;
using Xunit;

namespace HoaSap.Tests;

[Collection("api")]
public sealed class AdminLifecycleSafetyTests(ApiFixture fixture) : ApiTestBase(fixture)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AdminSecurityChangeRevokesRefreshAndOldAccess(bool changeRole)
    {
        var email = $"admin-lifecycle-{Guid.NewGuid():N}@example.test";
        var phone = "098" + Random.Shared.Next(1000000, 9999999);
        var registered = await Client.PostAsync("/api/auth/register", Json(new { fullName = "Security QA", email, phoneNumber = phone, password = "Password-123!" }));
        registered.EnsureSuccessStatusCode();
        using var auth = JsonDocument.Parse(await registered.Content.ReadAsStringAsync());
        var customerId = auth.RootElement.GetProperty("user").GetProperty("id").GetGuid();
        var refreshToken = auth.RootElement.GetProperty("refreshToken").GetString();
        using var customer = await AuthenticatedAsync(auth.RootElement.GetProperty("accessToken").GetString()!);
        using var admin = await AuthenticatedAsync(await AdminTokenAsync());
        var changed = changeRole
            ? await admin.PutAsync($"/api/management/accounts/{customerId}", Json(new { fullName = "Security QA", email, phoneNumber = phone, role = "Staff", isActive = true }))
            : await admin.PostAsync($"/api/management/accounts/{customerId}/reset-password", Json(new { newPassword = "Replacement-Password-456!" }));
        changed.EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client.PostAsync("/api/auth/refresh", Json(new { refreshToken }))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await customer.GetAsync("/api/auth/me")).StatusCode);
        var login = await Client.PostAsync("/api/auth/login", Json(new { email, password = changeRole ? "Password-123!" : "Replacement-Password-456!" }));
        login.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task ExistingCmsClientCannotSilentlyResetShippingWeight()
    {
        using var admin = await AuthenticatedAsync(await AdminTokenAsync());
        var code = "WEIGHT-" + Guid.NewGuid().ToString("N")[..8];
        var category = await admin.PostAsync("/api/management/categories", Json(new { code, name = "Shipping weight QA" }));
        category.EnsureSuccessStatusCode();
        using var cat = JsonDocument.Parse(await category.Content.ReadAsStringAsync());
        var categoryId = cat.RootElement.GetProperty("id").GetGuid();
        var product = await admin.PostAsync("/api/management/products", Json(new { categoryId, code, name = "Shipping QA", price = 100000m, originalPrice = 120000m, shippingWeightGrams = 900 }));
        product.EnsureSuccessStatusCode();
        using var created = JsonDocument.Parse(await product.Content.ReadAsStringAsync());
        var id = created.RootElement.GetProperty("id").GetGuid();
        var edited = await admin.PutAsync($"/api/management/products/{id}", Json(new { categoryId, code, name = "Edited by old client", price = 110000m, originalPrice = 120000m }));
        edited.EnsureSuccessStatusCode();
        using var saved = JsonDocument.Parse(await edited.Content.ReadAsStringAsync());
        Assert.Equal(900, saved.RootElement.GetProperty("shippingWeightGrams").GetInt32());
        Assert.False(saved.RootElement.GetProperty("packagingSpecsConfirmed").GetBoolean());
        Assert.Equal(JsonValueKind.Null, saved.RootElement.GetProperty("packageLengthCm").ValueKind);
        var list = await admin.GetAsync($"/api/management/products?search={code}");
        using var listing = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
        Assert.Equal(900, listing.RootElement.GetProperty("items")[0].GetProperty("shippingWeightGrams").GetInt32());
        var invalid = await admin.PutAsync($"/api/management/products/{id}", Json(new { categoryId, code, name = "Invalid weight", price = 110000m, shippingWeightGrams = 0 }));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }
}
