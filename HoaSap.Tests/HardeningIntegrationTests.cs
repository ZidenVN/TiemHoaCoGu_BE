using System.Net;
using System.Text.Json;
using HoaSap.API.Security;
using HoaSap.BLL.Workshops;
using HoaSap.DAL;
using HoaSap.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace HoaSap.Tests;

public sealed class HardeningIntegrationTests
{
    static StringContent Json(object value) => new(JsonSerializer.Serialize(value), System.Text.Encoding.UTF8, "application/json");
    static async Task<(ApiFixture Fixture, HttpClient Admin)> StartAsync()
    {
        var f = new ApiFixture();
        var c = f.CreateClient();
        var r = await c.PostAsync("/api/auth/login", Json(new { email = ApiFixture.AdminEmail, password = ApiFixture.AdminPassword }));
        r.EnsureSuccessStatusCode();
        using var d = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
        c.DefaultRequestHeaders.Authorization = new("Bearer", d.RootElement.GetProperty("accessToken").GetString());
        return (f, c);
    }
    static async Task<HttpClient> CustomerAsync(ApiFixture f)
    {
        var c = f.CreateClient(); var email = $"qa-{Guid.NewGuid():N}@example.test";
        var r = await c.PostAsync("/api/auth/register", Json(new { fullName = "QA", phoneNumber = "09" + Random.Shared.Next(100000000, 999999999), email, password = "Password-123!", role = "Admin" }));
        r.EnsureSuccessStatusCode(); using var d = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
        c.DefaultRequestHeaders.Authorization = new("Bearer", d.RootElement.GetProperty("accessToken").GetString()); return c;
    }
    static async Task<(Guid ProductId, decimal Price)> ProductAsync(HttpClient admin, decimal price = 100000m, int stock = 2)
    {
        var cat = await admin.PostAsync("/api/management/categories", Json(new { code = "T-" + Guid.NewGuid().ToString("N")[..8], name = "Test" })); cat.EnsureSuccessStatusCode();
        using var cd = JsonDocument.Parse(await cat.Content.ReadAsStringAsync()); var categoryId = cd.RootElement.GetProperty("id").GetGuid();
        var p = await admin.PostAsync("/api/management/products", Json(new { code = "P-" + Guid.NewGuid().ToString("N")[..8], name = "Test Flower", categoryId, price, isActive = true })); p.EnsureSuccessStatusCode();
        using var pd = JsonDocument.Parse(await p.Content.ReadAsStringAsync()); var id = pd.RootElement.GetProperty("id").GetGuid();
        (await admin.PutAsync($"/api/management/inventory/{id}?quantity={stock}", Json(new { }))).EnsureSuccessStatusCode(); return (id, price);
    }
    static async Task<(Guid Id, string Destination)> ShippingQuoteAsync(HttpClient customer, Guid productId, int quantity = 1)
    {
        var response = await customer.PostAsync("/api/shipping/quote", Json(new { streetAddress = "QA street", provinceId = 1000001, wardId = 1003580, items = new[] { new { productId, quantity } } }));
        response.EnsureSuccessStatusCode(); using var d = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return (d.RootElement.GetProperty("id").GetGuid(), d.RootElement.GetProperty("destination").GetString()!);
    }
    static async Task<(Guid WorkshopId, Guid SessionId)> WorkshopAsync(HttpClient admin, int capacity = 2)
    {
        var w = await admin.PostAsync("/api/workshops", Json(new { code = "W-" + Guid.NewGuid().ToString("N")[..8], name = "QA Workshop", basePrice = 100000m, duration = 60, maxCapacity = capacity, isActive = true })); w.EnsureSuccessStatusCode();
        using var wd = JsonDocument.Parse(await w.Content.ReadAsStringAsync()); var wid = wd.RootElement.GetProperty("id").GetGuid();
        var date = DateTime.UtcNow.AddDays(2).Date;
        var s = await admin.PostAsync($"/api/workshops/{wid}/sessions", Json(new { sessionDate = date, startTime = "10:00:00", endTime = "11:00:00", location = "QA", capacity })); s.EnsureSuccessStatusCode();
        using var sd = JsonDocument.Parse(await s.Content.ReadAsStringAsync()); return (wid, sd.RootElement.GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task OwnOrder_CannotReadAnotherCustomersOrder()
    {
        var (f, admin) = await StartAsync(); using (f) using (admin)
        {
            var p = await ProductAsync(admin); using var owner = await CustomerAsync(f); using var other = await CustomerAsync(f);
            var quote = await ShippingQuoteAsync(owner, p.ProductId);
            var created = await owner.PostAsync("/api/orders/checkout", Json(new { shippingAddress = quote.Destination, shippingQuoteId = quote.Id, items = new[] { new { productId = p.ProductId, quantity = 1 } } })); created.EnsureSuccessStatusCode(); using var d = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
            var id = d.RootElement.GetProperty("id").GetGuid(); var response = await other.GetAsync($"/api/orders/{id}"); Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
    }

    [Fact]
    public async Task CustomerCancel_Twice_RestoresReservedStockOnlyOnce()
    {
        var (f, admin) = await StartAsync(); using (f) using (admin)
        {
            var p = await ProductAsync(admin, stock: 3); using var customer = await CustomerAsync(f);
            var quote = await ShippingQuoteAsync(customer, p.ProductId, 2);
            var order = await customer.PostAsync("/api/orders/checkout", Json(new { shippingAddress = quote.Destination, shippingQuoteId = quote.Id, items = new[] { new { productId = p.ProductId, quantity = 2 } } })); order.EnsureSuccessStatusCode(); using var od = JsonDocument.Parse(await order.Content.ReadAsStringAsync()); var id = od.RootElement.GetProperty("id").GetGuid();
            Assert.Equal(HttpStatusCode.NoContent, (await customer.PostAsync($"/api/orders/{id}/cancel", null)).StatusCode); Assert.Equal(HttpStatusCode.NoContent, (await customer.PostAsync($"/api/orders/{id}/cancel", null)).StatusCode);
            using var scope = f.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<HoaSapDbContext>(); var inv = await db.Inventories.SingleAsync(x => x.ProductId == p.ProductId); Assert.Equal(3, inv.QuantityAvailable); Assert.Equal(0, inv.QuantityReserved);
        }
    }

    [Fact]
    public async Task StaffCancel_ThenRepeat_DoesNotReleaseStockTwice()
    {
        var (f, admin) = await StartAsync(); using (f) using (admin)
        {
            var p = await ProductAsync(admin, stock: 2); using var customer = await CustomerAsync(f); var quote = await ShippingQuoteAsync(customer, p.ProductId); var order = await customer.PostAsync("/api/orders/checkout", Json(new { shippingAddress = quote.Destination, shippingQuoteId = quote.Id, items = new[] { new { productId = p.ProductId, quantity = 1 } } })); order.EnsureSuccessStatusCode(); using var d = JsonDocument.Parse(await order.Content.ReadAsStringAsync()); var id = d.RootElement.GetProperty("id").GetGuid();
            Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsync($"/api/orders/{id}/status", Json(new { status = "Cancelled" }))).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsync($"/api/orders/{id}/status", Json(new { status = "Cancelled" }))).StatusCode);
            using var scope = f.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<HoaSapDbContext>(); Assert.Equal(2, (await db.Inventories.SingleAsync(x => x.ProductId == p.ProductId)).QuantityAvailable);
        }
    }

    [Fact]
    public async Task Delivery_DuplicateLines_AreSummedAndCannotOverallocate()
    {
        var (f, admin) = await StartAsync(); using (f) using (admin)
        {
            var p = await ProductAsync(admin, stock: 1); using var customer = await CustomerAsync(f); var quote = await ShippingQuoteAsync(customer, p.ProductId); var order = await customer.PostAsync("/api/orders/checkout", Json(new { shippingAddress = quote.Destination, shippingQuoteId = quote.Id, items = new[] { new { productId = p.ProductId, quantity = 1 } } })); order.EnsureSuccessStatusCode(); using var od = JsonDocument.Parse(await order.Content.ReadAsStringAsync()); var id = od.RootElement.GetProperty("id").GetGuid();
            Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsync($"/api/orders/{id}/status", Json(new { status = "Processing" }))).StatusCode); using var loaded = JsonDocument.Parse(await order.Content.ReadAsStringAsync()); var item = loaded.RootElement.GetProperty("items")[0].GetProperty("id").GetGuid();
            var response = await admin.PostAsync($"/api/orders/{id}/deliveries", Json(new { items = new[] { new { orderItemId = item, quantity = 1 }, new { orderItemId = item, quantity = 1 } } })); Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        }
    }

    [Fact]
    public async Task WorkshopCapacity_RejectsBookingBeyondCapacity()
    {
        var (f, admin) = await StartAsync(); using (f) using (admin) { var x = await WorkshopAsync(admin, 1); using var a = await CustomerAsync(f); using var b = await CustomerAsync(f); (await (a).PostAsync($"/api/bookings/sessions/{x.SessionId}", Json(new { ticketCount = 1, customerName = "A", customerPhone = "0901234567" }))).EnsureSuccessStatusCode(); Assert.Equal(HttpStatusCode.Conflict, (await b.PostAsync($"/api/bookings/sessions/{x.SessionId}", Json(new { ticketCount = 1, customerName = "B", customerPhone = "0901234568" }))).StatusCode); }
    }

    [Fact]
    public async Task ExpiredWorkshopHold_ReleasesCapacityOnce()
    {
        var (f, admin) = await StartAsync(); using (f) using (admin)
        {
            var x = await WorkshopAsync(admin, 2); using var customer = await CustomerAsync(f); var r = await customer.PostAsync($"/api/bookings/sessions/{x.SessionId}", Json(new { ticketCount = 2, customerName = "A", customerPhone = "0901234567" })); r.EnsureSuccessStatusCode(); using var d = JsonDocument.Parse(await r.Content.ReadAsStringAsync()); var bid = d.RootElement.GetProperty("id").GetGuid(); using (var scope = f.Services.CreateScope()) { var db = scope.ServiceProvider.GetRequiredService<HoaSapDbContext>(); var b = await db.WorkshopBookings.FindAsync(bid); b!.HoldExpiresAt = DateTime.UtcNow.AddMinutes(-1); await db.SaveChangesAsync(); }
            using (var scope = f.Services.CreateScope()) { await scope.ServiceProvider.GetRequiredService<WorkshopService>().ExpirePendingBookingsAsync(); await scope.ServiceProvider.GetRequiredService<WorkshopService>().ExpirePendingBookingsAsync(); }
            using var verify = f.Services.CreateScope(); var ctx = verify.ServiceProvider.GetRequiredService<HoaSapDbContext>(); Assert.Equal(0, (await ctx.WorkshopSessions.FindAsync(x.SessionId))!.BookedSlots); Assert.Equal("expired", (await ctx.WorkshopBookings.FindAsync(bid))!.BookingStatus);
        }
    }

    [Fact]
    public async Task BookingPayment_DepositThenBalance_AndChangedDuplicateTransactionIsRejected()
    {
        var (f, admin) = await StartAsync(); using (f) using (admin)
        {
            var x = await WorkshopAsync(admin); using var customer = await CustomerAsync(f); var r = await customer.PostAsync($"/api/bookings/sessions/{x.SessionId}", Json(new { ticketCount = 1, customerName = "A", customerPhone = "0901234567" })); r.EnsureSuccessStatusCode(); using var d = JsonDocument.Parse(await r.Content.ReadAsStringAsync()); var id = d.RootElement.GetProperty("id").GetGuid();
            Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/payments/booking/{id}", Json(new { transactionCode = "TX-1", paymentMethod = "Cash", amount = 30000m }))).StatusCode); Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/payments/booking/{id}", Json(new { transactionCode = "TX-2", paymentMethod = "Cash", amount = 70000m }))).StatusCode); Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync($"/api/payments/booking/{id}", Json(new { transactionCode = "TX-2", paymentMethod = "Cash", amount = 1m }))).StatusCode);
        }
    }

    [Fact]
    public async Task ConfirmedBooking_CanPayRemainingAfterHoldExpiry_WhenSessionIsNotNear()
    {
        var (f, admin) = await StartAsync(); using (f) using (admin)
        {
            var x = await WorkshopAsync(admin); using var customer = await CustomerAsync(f);
            var r = await customer.PostAsync($"/api/bookings/sessions/{x.SessionId}", Json(new { ticketCount = 1, customerName = "A", customerPhone = "0901234567" })); r.EnsureSuccessStatusCode();
            using var d = JsonDocument.Parse(await r.Content.ReadAsStringAsync()); var id = d.RootElement.GetProperty("id").GetGuid();
            (await admin.PostAsync($"/api/payments/booking/{id}", Json(new { transactionCode = "TX-C1", paymentMethod = "Cash", amount = 30000m }))).EnsureSuccessStatusCode();
            using (var scope = f.Services.CreateScope()) { var db = scope.ServiceProvider.GetRequiredService<HoaSapDbContext>(); var booking = await db.WorkshopBookings.FindAsync(id); booking!.HoldExpiresAt = DateTime.UtcNow.AddMinutes(-1); await db.SaveChangesAsync(); }
            Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/payments/booking/{id}", Json(new { transactionCode = "TX-C2", paymentMethod = "Cash", amount = 70000m }))).StatusCode);
        }
    }

    [Fact]
    public async Task CheckIn_WithDepositOnly_IsRejected()
    {
        var (f, admin) = await StartAsync(); using (f) using (admin) { var x = await WorkshopAsync(admin); using var customer = await CustomerAsync(f); var r = await customer.PostAsync($"/api/bookings/sessions/{x.SessionId}", Json(new { ticketCount = 1, customerName = "A", customerPhone = "0901234567" })); using var d = JsonDocument.Parse(await r.Content.ReadAsStringAsync()); var id = d.RootElement.GetProperty("id").GetGuid(); (await admin.PostAsync($"/api/payments/booking/{id}", Json(new { transactionCode = "TX-D", paymentMethod = "Cash", amount = 30000m }))).EnsureSuccessStatusCode(); Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync($"/api/bookings/{id}/check-in", null)).StatusCode); }
    }

    [Fact]
    public async Task Refund_CannotExceedPaidAmount()
    {
        var (f, admin) = await StartAsync(); using (f) using (admin) { var x = await WorkshopAsync(admin); using var customer = await CustomerAsync(f); var r = await customer.PostAsync($"/api/bookings/sessions/{x.SessionId}", Json(new { ticketCount = 1, customerName = "A", customerPhone = "0901234567" })); using var d = JsonDocument.Parse(await r.Content.ReadAsStringAsync()); var id = d.RootElement.GetProperty("id").GetGuid(); (await admin.PostAsync($"/api/payments/booking/{id}", Json(new { transactionCode = "TX-R", paymentMethod = "Cash", amount = 30000m }))).EnsureSuccessStatusCode(); Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync($"/api/bookings/{id}/refund", Json(new { amount = 30001m, reason = "QA" }))).StatusCode); }
    }

    [Fact] public void PasswordVerify_WrongPasswordFails() => Assert.False(PasswordService.Verify("wrong", PasswordService.Hash("correct")));
    [Fact] public void PasswordVerify_CorrectPasswordSucceeds() => Assert.True(PasswordService.Verify("correct", PasswordService.Hash("correct")));
    [Fact] public void PasswordVerify_MalformedHashFailsWithoutThrowing() => Assert.False(PasswordService.Verify("anything", "not-a-valid-hash"));
}
