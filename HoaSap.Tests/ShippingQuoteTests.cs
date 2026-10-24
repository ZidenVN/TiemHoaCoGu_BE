using HoaSap.BLL;
using HoaSap.BLL.Commerce;
using HoaSap.DAL;
using HoaSap.Domain.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace HoaSap.Tests;

public sealed class ShippingQuoteTests
{
    private sealed class FakeCarrier : ICarrierAdapter
    {
        public CarrierRateRequest? Last { get; private set; }
        public Task<CarrierRateResult> QuoteAsync(CarrierRateRequest request, CancellationToken ct = default)
        {
            Last = request;
            return Task.FromResult(new CarrierRateResult(true, 42000m, null, null));
        }
    }
    private sealed class Store : IAsyncDisposable
    {
        public SqliteConnection Connection { get; } = new("Data Source=:memory:");
        public HoaSapDbContext Db { get; private set; } = null!;
        public Customer Customer { get; } = new() { Email = "ship@example.test", FullName = "Receiver", PhoneNumber = "0900000003" };
        public Product Product { get; private set; } = null!;
        public FakeCarrier Carrier { get; } = new();
        public ShippingQuoteService Shipping => new(Db, Carrier, Options.Create(new CarrierOptions { Origin = "Warehouse A", QuoteLifetimeMinutes = 10 }));
        public static async Task<Store> CreateAsync()
        {
            var s = new Store(); await s.Connection.OpenAsync();
            s.Db = new HoaSapDbContext(new DbContextOptionsBuilder<HoaSapDbContext>().UseSqlite(s.Connection).Options);
            await s.Db.Database.EnsureCreatedAsync();
            var category = new Category { Code = "SHIP", Name = "Shipping" };
            s.Product = new Product { CategoryId = category.Id, Code = "SHIP-P", Name = "Box", Price = 100000m, ShippingWeightGrams = 750 };
            s.Db.AddRange(s.Customer, category, s.Product, new Inventory { ProductId = s.Product.Id, QuantityAvailable = 10 }, new ShoppingCartItem { CustomerId = s.Customer.Id, ProductId = s.Product.Id, Quantity = 2 });
            await s.Db.SaveChangesAsync(); return s;
        }
        public async ValueTask DisposeAsync() { await Db.DisposeAsync(); await Connection.DisposeAsync(); }
    }

    [Fact]
    public async Task Quote_uses_server_sku_weight_not_client_weight_and_persists_amount()
    {
        await using var s = await Store.CreateAsync();
        var quote = await s.Shipping.QuoteAsync(s.Customer.Id, new("42 Test Street", 1000001, "Hồ Chí Minh", 1003580, "Phường Thủ Đức", 0.001m));
        Assert.Equal(42000m, quote.Amount); Assert.Equal("Warehouse A", quote.Origin); Assert.Equal(1.5m, s.Carrier.Last!.Weight);
        Assert.Equal("42 Test Street, Phường Thủ Đức, Hồ Chí Minh", quote.Destination);
        var stored = await s.Db.ShippingQuotes.SingleAsync(); Assert.Equal(s.Customer.Id, stored.CustomerId); Assert.Null(stored.OrderId); Assert.Equal(64, stored.CartFingerprint.Length);
    }

    [Fact]
    public async Task Empty_or_invalid_cart_is_rejected_before_carrier_call()
    {
        await using var s = await Store.CreateAsync();
        await Assert.ThrowsAsync<BusinessException>(() => s.Shipping.QuoteAsync(Guid.NewGuid(), new("42 Test Street", 1000001, "Hồ Chí Minh", 1003580, "Phường Thủ Đức")));
        await Assert.ThrowsAsync<BusinessException>(() => s.Shipping.QuoteAsync(s.Customer.Id, new("42 Test Street", 1000001, "Hồ Chí Minh", 1003580, "Phường Thủ Đức", Items: [new CartItemRequest { ProductId = s.Product.Id, Quantity = 0 }])));
        Assert.Null(s.Carrier.Last);
    }

    [Fact]
    public async Task Disabled_carrier_fails_closed_without_persisting_a_quote()
    {
        await using var s = await Store.CreateAsync();
        var service = new ShippingQuoteService(s.Db, new DisabledCarrier(), Options.Create(new CarrierOptions { Origin = "Warehouse A" }));
        var error = await Assert.ThrowsAsync<BusinessException>(() => service.QuoteAsync(s.Customer.Id, new("42 Test Street", 1000001, "Hồ Chí Minh", 1003580, "Phường Thủ Đức")));
        Assert.Equal(503, error.StatusCode);
        Assert.Empty(await s.Db.ShippingQuotes.ToListAsync());
    }

    [Fact]
    public async Task Checkout_consumes_exact_server_quote_and_replay_returns_same_order()
    {
        await using var s = await Store.CreateAsync();
        var quote = await s.Shipping.QuoteAsync(s.Customer.Id, new("42 Test Street", 1000001, "Hồ Chí Minh", 1003580, "Phường Thủ Đức"));
        var service = new CommerceService(s.Db);
        var request = new CheckoutRequest { ShippingAddress = quote.Destination, ShippingQuoteId = quote.Id, IdempotencyKey = "shipping-checkout" };
        var order = await service.CheckoutAsync(s.Customer.Id, request);
        var replay = await service.CheckoutAsync(s.Customer.Id, request);
        Assert.Equal(42000m, order.ShippingFee); Assert.Equal(242000m, order.TotalAmount); Assert.Equal(order.Id, replay.Id);
        Assert.NotNull((await s.Db.ShippingQuotes.SingleAsync()).UsedAt);
        Assert.Equal(order.Id, (await s.Db.ShippingQuotes.SingleAsync()).OrderId);
    }

    [Fact]
    public async Task Changed_cart_or_destination_cannot_reuse_a_cheaper_quote()
    {
        await using var s = await Store.CreateAsync();
        var quote = await s.Shipping.QuoteAsync(s.Customer.Id, new("42 Test Street", 1000001, "Hồ Chí Minh", 1003580, "Phường Thủ Đức"));
        var service = new CommerceService(s.Db);
        var wrongAddress = new CheckoutRequest { ShippingAddress = "Other destination", ShippingQuoteId = quote.Id, IdempotencyKey = "wrong-address" };
        Assert.Equal(409, (await Assert.ThrowsAsync<BusinessException>(() => service.CheckoutAsync(s.Customer.Id, wrongAddress))).StatusCode);
        var cart = await s.Db.ShoppingCartItems.SingleAsync(); cart.Quantity = 3; await s.Db.SaveChangesAsync();
        var changed = new CheckoutRequest { ShippingAddress = quote.Destination, ShippingQuoteId = quote.Id, IdempotencyKey = "changed-cart" };
        Assert.Equal(409, (await Assert.ThrowsAsync<BusinessException>(() => service.CheckoutAsync(s.Customer.Id, changed))).StatusCode);
        Assert.Empty(await s.Db.Orders.ToListAsync()); Assert.Equal(10, (await s.Db.Inventories.SingleAsync()).QuantityAvailable);
    }
}
