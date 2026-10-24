using System.Data;
using System.Text.Json;
using HoaSap.BLL;
using HoaSap.BLL.Commerce;
using HoaSap.DAL;
using HoaSap.DAL.Auditing;
using HoaSap.Domain.Entities;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;
using Xunit.Abstractions;

namespace HoaSap.Tests;

public sealed class SqlServerBulkAuditTests
{
    private readonly ITestOutputHelper _output;

    public SqlServerBulkAuditTests(ITestOutputHelper output) => _output = output;

    private sealed class FixedActor(Guid id, string role, string traceId) : IAuditActor
    {
        public Guid? ActorId { get; } = id;
        public string? Role { get; } = role;
        public string? TraceId { get; } = traceId;
    }

    private sealed class SyntheticSqlCarrier : ICarrierAdapter
    {
        public Task<CarrierRateResult> QuoteAsync(CarrierRateRequest request, CancellationToken ct = default) =>
            Task.FromResult(new CarrierRateResult(true, 42000m, null, null));
    }

    private sealed record Seed(Guid CustomerId, Guid ProductId, Guid InventoryId, Guid VoucherId, string VoucherCode);
    private sealed record Audit(string EntityType, string EntityId, string Action, string OldJson, string NewJson, Guid? ActorId, string? ActorRole, string? TraceId);
    private sealed class AsyncGate
    {
        private readonly TaskCompletionSource<bool> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrivals;

        public Task ArriveAndWaitAsync()
        {
            if (Interlocked.Increment(ref _arrivals) == 2) _ready.TrySetResult(true);
            return _ready.Task;
        }
    }
    private sealed class SqlFixture : IAsyncDisposable
    {
        private readonly SqlConnectionStringBuilder _builder;
        public string DatabaseName { get; }
        public string ConnectionString => _builder.ConnectionString;
        public FixedActor Actor { get; } = new(Guid.Parse("11111111-1111-1111-1111-111111111111"), "IntegrationTest", "sql-bulk-audit-trace");

        private SqlFixture(SqlConnectionStringBuilder builder)
        {
            _builder = builder;
            DatabaseName = builder.InitialCatalog;
        }

        public static async Task<SqlFixture> CreateAsync(ITestOutputHelper output)
        {
            var configured = Environment.GetEnvironmentVariable("HOASAP_TEST_SQLSERVER");
            if (string.IsNullOrWhiteSpace(configured)) throw new InvalidOperationException("HOASAP_TEST_SQLSERVER is required.");
            var builder = new SqlConnectionStringBuilder(configured) { InitialCatalog = $"HoaSapSqlAudit_{Guid.NewGuid():N}" };
            var fixture = new SqlFixture(builder);
            try
            {
                await using var db = fixture.CreateContext();
                await db.Database.MigrateAsync();
                var version = await db.Database.SqlQueryRaw<string>("SELECT @@VERSION AS [Value]").SingleAsync();
                output.WriteLine($"SQL Server @@VERSION ({fixture.DatabaseName}): {version}");
                return fixture;
            }
            catch
            {
                await fixture.DisposeAsync();
                throw;
            }
        }

        public HoaSapDbContext CreateContext(IAuditActor? actor = null)
        {
            var options = new DbContextOptionsBuilder<HoaSapDbContext>().UseSqlServer(ConnectionString).Options;
            return new HoaSapDbContext(options, actor ?? Actor);
        }

        public async ValueTask DisposeAsync()
        {
            await using var db = CreateContext();
            try { await db.Database.EnsureDeletedAsync(); }
            finally
            {
                using var poolKey = new SqlConnection(ConnectionString);
                SqlConnection.ClearPool(poolKey);
            }
        }
    }

    [SqlServerFact]
    public async Task CheckoutWithVoucher_WritesExactAtomicInventoryAndVoucherAudits_AndReplayDoesNotAddAudit()
    {
        await using var fixture = await SqlFixture.CreateAsync(_output);
        await using var db = fixture.CreateContext();
        var seed = await SeedAsync(db, stock: 2, voucherLimit: 3);
        var service = new CommerceService(db);
        var quote = await QuoteAsync(db, seed.CustomerId, seed.ProductId);
        var request = Request(seed.ProductId, quote, seed.VoucherCode);
        request.IdempotencyKey = "checkout-replay";

        var order = await service.CheckoutAsync(seed.CustomerId, request);
        var replay = await service.CheckoutAsync(seed.CustomerId, request);

        Assert.Equal(order.Id, replay.Id);
        var inventoryLogs = await AuditsAsync(db, seed.InventoryId);
        var voucherLogs = await AuditsAsync(db, seed.VoucherId);
        Assert.Single(inventoryLogs);
        Assert.Single(voucherLogs);
        AssertAudit(inventoryLogs[0], "Inventory", seed.InventoryId, 2, 1, 0, 1);
        AssertAudit(voucherLogs[0], "Voucher", seed.VoucherId, 0, 1);
        AssertActor(inventoryLogs[0], fixture.Actor);
        AssertActor(voucherLogs[0], fixture.Actor);
        Assert.Equal(1, await db.Orders.CountAsync());
    }

    [SqlServerFact]
    public async Task InvalidVoucher_AfterInventoryMutation_RollsBackStockOrdersAndAllCustomerAudits()
    {
        await using var fixture = await SqlFixture.CreateAsync(_output);
        await using var db = fixture.CreateContext();
        var seed = await SeedAsync(db, stock: 1);
        var service = new CommerceService(db);

        var quote = await QuoteAsync(db, seed.CustomerId, seed.ProductId);
        var error = await Assert.ThrowsAsync<BusinessException>(() => service.CheckoutAsync(seed.CustomerId, Request(seed.ProductId, quote, "NOT-A-VOUCHER")));

        Assert.Equal(400, error.StatusCode);
        var inventory = await db.Inventories.SingleAsync(x => x.Id == seed.InventoryId);
        Assert.Equal(1, inventory.QuantityAvailable);
        Assert.Equal(0, inventory.QuantityReserved);
        Assert.Empty(await db.Orders.ToListAsync());
        Assert.Empty(await AuditsAsync(db, seed.InventoryId));
        Assert.Empty(await AuditsAsync(db, seed.VoucherId));
    }

    [SqlServerFact]
    public async Task Cancellation_ReleasesReservationOnlyOnce_WhenRepeated()
    {
        await using var fixture = await SqlFixture.CreateAsync(_output);
        await using var db = fixture.CreateContext();
        var seed = await SeedAsync(db, stock: 1);
        var service = new CommerceService(db);
        var quote = await QuoteAsync(db, seed.CustomerId, seed.ProductId);
        var order = await service.CheckoutAsync(seed.CustomerId, Request(seed.ProductId, quote));

        await service.CancelAsync(seed.CustomerId, order.Id);
        await service.CancelAsync(seed.CustomerId, order.Id);

        var inventory = await db.Inventories.SingleAsync(x => x.Id == seed.InventoryId);
        Assert.Equal(1, inventory.QuantityAvailable);
        Assert.Equal(0, inventory.QuantityReserved);
        var logs = await AuditsAsync(db, seed.InventoryId);
        Assert.Equal(2, logs.Count);
        AssertAudit(logs[0], "Inventory", seed.InventoryId, 1, 0, 0, 1);
        AssertAudit(logs[1], "Inventory", seed.InventoryId, 0, 1, 1, 0);
    }

    [SqlServerFact]
    public async Task Fulfillment_ConsumesReservationOnlyOnce_WhenCompletedIsRepeated()
    {
        await using var fixture = await SqlFixture.CreateAsync(_output);
        await using var db = fixture.CreateContext();
        var seed = await SeedAsync(db, stock: 1);
        var service = new CommerceService(db);
        var quote = await QuoteAsync(db, seed.CustomerId, seed.ProductId);
        var order = await service.CheckoutAsync(seed.CustomerId, Request(seed.ProductId, quote));
        var itemId = order.Items.Single().Id;
        await service.TransitionAsync(order.Id, "Processing");
        var delivery = await service.DeliveryAsync(order.Id, new DeliveryRequest { Items = [new DeliveryLineRequest { OrderItemId = itemId, Quantity = 1 }] });
        await service.TransitionDeliveryAsync(order.Id, delivery.Id, "Shipped");
        await service.TransitionDeliveryAsync(order.Id, delivery.Id, "Delivered");
        db.Payments.Add(new Payment { OrderId = order.Id, TransactionCode = $"PAY-{Guid.NewGuid():N}", PaymentMethod = "Cash", PaymentType = "Order", Amount = order.TotalAmount, PaymentStatus = "Paid", PaidAt = DateTime.UtcNow });
        var paidOrder = await db.Orders.SingleAsync(x => x.Id == order.Id);
        paidOrder.PaymentStatus = "Paid";
        await db.SaveChangesAsync();
        await service.TransitionAsync(order.Id, "Delivering");
        await service.TransitionAsync(order.Id, "Completed");
        await service.TransitionAsync(order.Id, "Completed");

        var inventory = await db.Inventories.SingleAsync(x => x.Id == seed.InventoryId);
        Assert.Equal(0, inventory.QuantityAvailable);
        Assert.Equal(0, inventory.QuantityReserved);
        var logs = await AuditsAsync(db, seed.InventoryId);
        Assert.Equal(2, logs.Count);
        AssertAudit(logs[0], "Inventory", seed.InventoryId, 1, 0, 0, 1);
        AssertAudit(logs[1], "Inventory", seed.InventoryId, 0, 0, 1, 0);
    }

    [SqlServerFact]
    public async Task Refund_ReleasesReservationAndDoesNotReleaseItAgain()
    {
        await using var fixture = await SqlFixture.CreateAsync(_output);
        await using var db = fixture.CreateContext();
        var seed = await SeedAsync(db, stock: 1);
        var commerce = new CommerceService(db);
        var quote = await QuoteAsync(db, seed.CustomerId, seed.ProductId);
        var order = await commerce.CheckoutAsync(seed.CustomerId, Request(seed.ProductId, quote));
        db.Payments.Add(new Payment { OrderId = order.Id, TransactionCode = $"PAY-{Guid.NewGuid():N}", PaymentMethod = "Cash", PaymentType = "Order", Amount = order.TotalAmount, PaymentStatus = "Paid", PaidAt = DateTime.UtcNow });
        var paidOrder = await db.Orders.SingleAsync(x => x.Id == order.Id);
        paidOrder.PaymentStatus = "Paid";
        await db.SaveChangesAsync();
        var refunds = new RefundService(db);

        var requested = await refunds.RequestOrderAsync(order.Id, new RefundRequestDto { Amount = order.TotalAmount, Reason = "integration return" });
        await refunds.ApproveAsync(requested.Id, "audit-admin");
        await refunds.ProcessAsync(requested.Id, $"REF-{Guid.NewGuid():N}");

        var inventory = await db.Inventories.SingleAsync(x => x.Id == seed.InventoryId);
        Assert.Equal(1, inventory.QuantityAvailable);
        Assert.Equal(0, inventory.QuantityReserved);
        var logs = await AuditsAsync(db, seed.InventoryId);
        Assert.Equal(2, logs.Count);
        AssertAudit(logs[1], "Inventory", seed.InventoryId, 0, 1, 1, 0);
        Assert.Equal("Processed", await db.Refunds.Where(x => x.Id == requested.Id).Select(x => x.RefundStatus).SingleAsync());
    }

    [SqlServerFact]
    public async Task ConcurrentCheckout_OneStockUnitProducesOneOrderAndOneReleaseAuditOnWinner()
    {
        await using var fixture = await SqlFixture.CreateAsync(_output);
        await using var seedDb = fixture.CreateContext();
        var first = await SeedAsync(seedDb, stock: 1);
        var secondCustomer = new Customer { Email = $"customer2-{Guid.NewGuid():N}@example.test", FullName = "Second", PhoneNumber = "0900000002", PasswordHash = "test" };
        seedDb.Customers.Add(secondCustomer);
        await seedDb.SaveChangesAsync();
        var barrier = new AsyncGate();
        var outcomes = await Task.WhenAll(CheckoutOnBarrierAsync(fixture, first.CustomerId, first.ProductId, barrier), CheckoutOnBarrierAsync(fixture, secondCustomer.Id, first.ProductId, barrier));

        Assert.Single(outcomes, x => x.Success);
        Assert.All(outcomes, x => Assert.True(x.Success || x.IsExpectedConflict, x.Exception?.ToString()));
        await using var verify = fixture.CreateContext();
        Assert.Equal(1, await verify.Orders.CountAsync());
        var inventory = await verify.Inventories.SingleAsync(x => x.Id == first.InventoryId);
        Assert.Equal(0, inventory.QuantityAvailable);
        Assert.Equal(1, inventory.QuantityReserved);
        var logs = await AuditsAsync(verify, first.InventoryId);
        Assert.Single(logs);
        AssertAudit(logs[0], "Inventory", first.InventoryId, 1, 0, 0, 1);
    }

    [SqlServerFact]
    public async Task ConcurrentDifferentProducts_SharedVoucherLimitOneHasOneWinnerAndOneVoucherAudit()
    {
        await using var fixture = await SqlFixture.CreateAsync(_output);
        await using var db = fixture.CreateContext();
        var first = await SeedAsync(db, stock: 5, voucherLimit: 1);
        var category = new Category { Code = $"CAT-{Guid.NewGuid():N}", Name = "Race" };
        // Synthetic package values exist only for the isolated fake-carrier concurrency test.
        var product = new Product { CategoryId = category.Id, Code = $"PROD-{Guid.NewGuid():N}", Name = "Race product", Price = 100m, ShippingWeightGrams = 500, PackageLengthCm = 30m, PackageWidthCm = 20m, PackageHeightCm = 10m, PackagingSpecsConfirmed = true, IsActive = true };
        db.Categories.Add(category); db.Products.Add(product); db.Inventories.Add(new Inventory { ProductId = product.Id, QuantityAvailable = 5 });
        var secondCustomer = new Customer { Email = $"voucher2-{Guid.NewGuid():N}@example.test", FullName = "Second", PhoneNumber = "0900000003", PasswordHash = "test" };
        db.Customers.Add(secondCustomer); await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var barrier = new AsyncGate();
        var outcomes = await Task.WhenAll(CheckoutOnBarrierAsync(fixture, first.CustomerId, first.ProductId, barrier, first.VoucherCode), CheckoutOnBarrierAsync(fixture, secondCustomer.Id, product.Id, barrier, first.VoucherCode));

        Assert.Single(outcomes, x => x.Success);
        Assert.All(outcomes, x => Assert.True(x.Success || x.IsExpectedConflict, x.Exception?.ToString()));
        await using var verify = fixture.CreateContext();
        Assert.Equal(1, await verify.Orders.CountAsync());
        Assert.Equal(1, await verify.Vouchers.Where(x => x.Id == first.VoucherId).Select(x => x.UsedCount).SingleAsync());
        var voucherLog = Assert.Single(await AuditsAsync(verify, first.VoucherId));
        AssertAudit(voucherLog, "Voucher", first.VoucherId, 0, 1);
        var firstLogs = await AuditsAsync(verify, first.InventoryId);
        var secondInventoryId = await verify.Inventories.Where(x => x.ProductId == product.Id).Select(x => x.Id).SingleAsync();
        var secondLogs = await AuditsAsync(verify, secondInventoryId);
        Assert.Equal(1, firstLogs.Count + secondLogs.Count);
        var winner = Assert.Single(outcomes, x => x.Success);
        var inventories = await verify.Inventories.AsNoTracking().ToListAsync();
        foreach (var inventory in inventories)
        {
            Assert.Equal(inventory.ProductId == winner.ProductId ? 4 : 5, inventory.QuantityAvailable);
            Assert.Equal(inventory.ProductId == winner.ProductId ? 1 : 0, inventory.QuantityReserved);
        }
        var winningInventory = inventories.Single(x => x.ProductId == winner.ProductId);
        var winningLog = Assert.Single(firstLogs.Concat(secondLogs));
        AssertAudit(winningLog, "Inventory", winningInventory.Id, 5, 4, 0, 1);
        Assert.Equal(winner.ProductId, await verify.OrderItems.Select(x => x.ProductId).SingleAsync());
    }

    [SqlServerFact]
    public async Task AuditedBulkUpdate_RejectsReadCommittedBeforeMutation()
    {
        await using var fixture = await SqlFixture.CreateAsync(_output);
        await using var db = fixture.CreateContext();
        var seed = await SeedAsync(db, stock: 2);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted);

        await Assert.ThrowsAsync<InvalidOperationException>(() => db.ExecuteUpdateAuditedAsync(db.Inventories.Where(x => x.Id == seed.InventoryId), x => x.SetProperty(i => i.QuantityAvailable, i => i.QuantityAvailable - 1)));
        var inventory = await db.Inventories.AsNoTracking().SingleAsync(x => x.Id == seed.InventoryId);
        Assert.Equal(2, inventory.QuantityAvailable);
        Assert.Empty(await AuditsAsync(db, seed.InventoryId));
    }

    private static async Task<Seed> SeedAsync(HoaSapDbContext db, int stock, int voucherLimit = 10)
    {
        var category = new Category { Code = $"CAT-{Guid.NewGuid():N}", Name = "Integration" };
        // Synthetic package values exist only for this SQL integration fixture and its fake carrier.
        var product = new Product { CategoryId = category.Id, Code = $"PROD-{Guid.NewGuid():N}", Name = "Integration flower", Price = 100m, ShippingWeightGrams = 500, PackageLengthCm = 30m, PackageWidthCm = 20m, PackageHeightCm = 10m, PackagingSpecsConfirmed = true, IsActive = true };
        var inventory = new Inventory { ProductId = product.Id, QuantityAvailable = stock };
        var voucher = new Voucher { Code = $"V-{Guid.NewGuid():N}", Description = "Integration voucher", DiscountType = "FixedAmount", DiscountValue = 10m, StartDate = DateTime.UtcNow.AddHours(-1), EndDate = DateTime.UtcNow.AddHours(1), UsageLimit = voucherLimit, IsActive = true };
        var customer = new Customer { Email = $"customer-{Guid.NewGuid():N}@example.test", FullName = "Integration Customer", PhoneNumber = "0900000001", PasswordHash = "test" };
        db.AddRange(category, product, inventory, voucher, customer);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return new(customer.Id, product.Id, inventory.Id, voucher.Id, voucher.Code);
    }

    private static async Task<ShippingQuoteDto> QuoteAsync(HoaSapDbContext db, Guid customerId, Guid productId, int quantity = 1)
    {
        var service = new ShippingQuoteService(db, new SyntheticSqlCarrier(), Options.Create(new CarrierOptions { Origin = "Synthetic SQL test warehouse" }));
        return await service.QuoteAsync(customerId, new ShippingQuoteRequest("SQL integration street", 1000001, "Hồ Chí Minh", 1003580, "Phường Thủ Đức", Items: [new CartItemRequest { ProductId = productId, Quantity = quantity }]));
    }

    private static CheckoutRequest Request(Guid productId, ShippingQuoteDto quote, string? voucher = null) => new() { ShippingAddress = quote.Destination, ShippingQuoteId = quote.Id, VoucherCode = voucher, Items = [new CartItemRequest { ProductId = productId, Quantity = 1 }] };

    private static async Task<List<Audit>> AuditsAsync(HoaSapDbContext db, Guid entityId) => await db.AuditLogs.AsNoTracking().Where(x => x.EntityId == entityId.ToString() && x.Action == "Modified").OrderBy(x => x.OccurredAtUtc).Select(x => new Audit(x.EntityType, x.EntityId, x.Action, x.OldValuesJson, x.NewValuesJson, x.ActorId, x.ActorRole, x.TraceId)).ToListAsync();

    private static void AssertActor(Audit audit, FixedActor actor)
    {
        Assert.Equal(actor.ActorId, audit.ActorId);
        Assert.Equal(actor.Role, audit.ActorRole);
        Assert.Equal(actor.TraceId, audit.TraceId);
    }

    private static void AssertAudit(Audit audit, string type, Guid id, int oldAvailable, int newAvailable, int oldReserved, int newReserved)
    {
        Assert.Equal(type, audit.EntityType); Assert.Equal(id.ToString(), audit.EntityId); Assert.Equal("Modified", audit.Action);
        Assert.Equal(oldAvailable, Int(audit.OldJson, "QuantityAvailable")); Assert.Equal(newAvailable, Int(audit.NewJson, "QuantityAvailable"));
        Assert.Equal(oldReserved, Int(audit.OldJson, "QuantityReserved")); Assert.Equal(newReserved, Int(audit.NewJson, "QuantityReserved"));
    }

    private static void AssertAudit(Audit audit, string type, Guid id, int oldUsed, int newUsed)
    {
        Assert.Equal(type, audit.EntityType); Assert.Equal(id.ToString(), audit.EntityId); Assert.Equal("Modified", audit.Action);
        Assert.Equal(oldUsed, Int(audit.OldJson, "UsedCount")); Assert.Equal(newUsed, Int(audit.NewJson, "UsedCount"));
    }

    private static int Int(string json, string property)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty(property).GetInt32();
    }

    private sealed record Outcome(bool Success, Guid ProductId, Exception? Exception)
    {
        public bool IsExpectedConflict => Exception is BusinessException b && b.StatusCode is 400 or 409 || IsDeadlock(Exception);
    }

    private static async Task<Outcome> CheckoutOnBarrierAsync(SqlFixture fixture, Guid customerId, Guid productId, AsyncGate barrier, string? voucher = null)
    {
        await using var db = fixture.CreateContext();
        var service = new CommerceService(db);
        var quote = await QuoteAsync(db, customerId, productId);
        await barrier.ArriveAndWaitAsync();
        try { await service.CheckoutAsync(customerId, Request(productId, quote, voucher)); return new(true, productId, null); }
        catch (Exception ex) when (ex is BusinessException || IsDeadlock(ex) || ex is DbUpdateException) { return new(false, productId, ex); }
    }

    private static bool IsDeadlock(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
            if (current is SqlException sql && sql.Number == 1205) return true;
        return false;
    }
}
