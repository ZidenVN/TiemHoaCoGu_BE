using HoaSap.DAL;
using HoaSap.DAL.Maintenance;
using HoaSap.Domain;
using HoaSap.Domain.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace HoaSap.Tests;

public sealed class LegacyDataMigrationTests
{
    [Fact]
    public async Task Upgrade_from_real_13d90e6_schema_preserves_money_stock_voucher_and_account()
    {
        var root = Path.Combine(Path.GetTempPath(), "hoasap-legacy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "legacy.db");
        var backup = Path.Combine(root, "before.db");
        try
        {
            await using (var raw = new SqliteConnection($"Data Source={path};Foreign Keys=False;Pooling=False"))
            {
                await raw.OpenAsync();
                using var resource = typeof(LegacyDataMigrationTests).Assembly.GetManifestResourceStream("HoaSap.Tests.Fixtures.legacy-13d90e6.sql")!;
                using var reader = new StreamReader(resource);
                await using var command = raw.CreateCommand();
                command.CommandText = await reader.ReadToEndAsync();
                await command.ExecuteNonQueryAsync();
            }
            var options = new DbContextOptionsBuilder<HoaSapDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
            var plan = await SqliteSchemaUpgrade.UpgradeAsync(options, new SqliteUpgradeOptions(true, backup));
            Assert.True(plan.CanApply, string.Join("; ", plan.Refusals));
            Assert.True(File.Exists(backup));
            await using (var verify = new HoaSapDbContext(options))
            {
                var customer = await verify.Customers.SingleAsync();
                Assert.Equal("legacy-seeded@example.test", customer.Email); Assert.True(customer.IsActive);
                Assert.Equal("not-a-real-credential", customer.PasswordHash);
                var order = await verify.Orders.SingleAsync();
                Assert.Equal("LEGACY-ORD", order.OrderCode); Assert.Equal(720000m, order.TotalAmount); Assert.Equal("PartiallyPaid", order.PaymentStatus);
                Assert.Null(order.ShippingQuoteId);
                var payment = await verify.Payments.SingleAsync(); Assert.Equal(150000m, payment.Amount); Assert.Equal("Paid", payment.PaymentStatus);
                var inventory = await verify.Inventories.SingleAsync(); Assert.Equal(8, inventory.QuantityAvailable); Assert.Equal(2, inventory.QuantityReserved);
                Assert.Equal(1, (await verify.Vouchers.SingleAsync()).UsedCount);
                var legacyProduct = await verify.Products.SingleAsync();
                Assert.Null(legacyProduct.ShippingWeightGrams);
                Assert.Null(legacyProduct.PackageLengthCm); Assert.Null(legacyProduct.PackageWidthCm); Assert.Null(legacyProduct.PackageHeightCm);
                Assert.False(legacyProduct.PackagingSpecsConfirmed);
                Assert.Empty(await verify.GatewayPaymentIntents.ToListAsync());
                Assert.Empty(await verify.Set<RefreshToken>().ToListAsync());
                Assert.Empty(await verify.Set<CustomQuotationRevision>().ToListAsync());
                Assert.Empty(await verify.Set<NotificationOutbox>().ToListAsync());
            }
            var again = await SqliteSchemaUpgrade.UpgradeAsync(options, new SqliteUpgradeOptions(true, Path.Combine(root, "again.db")));
            Assert.True(again.CanApply); Assert.False(again.HasChanges);
            await using var old = new SqliteConnection($"Data Source={backup};Mode=ReadOnly;Pooling=False");
            await old.OpenAsync(); await using var check = old.CreateCommand();
            check.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Products') WHERE name = 'ShippingWeightGrams'";
            Assert.Equal(0L, (long)(await check.ExecuteScalarAsync())!);
            check.CommandText = "SELECT COUNT(*) FROM Orders";
            Assert.Equal(1L, (long)(await check.ExecuteScalarAsync())!);
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
    }
}
