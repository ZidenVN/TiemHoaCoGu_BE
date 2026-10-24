using System.Text.Json;
using HoaSap.Domain;
using HoaSap.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace HoaSap.DAL.Auditing;

internal static class AuditRecorder
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly IReadOnlyDictionary<Type, string[]> Fields = new Dictionary<Type, string[]>
    {
        [typeof(Customer)] = ["Role", "IsActive", "IsGuest", "CreatedAt"],
        [typeof(Category)] = ["Code", "Name"],
        [typeof(Product)] = ["CategoryId", "Code", "Name", "Price", "OriginalPrice", "Badge", "Section", "IsCustomizable", "IsPreOrder", "ShippingWeightGrams", "PackageLengthCm", "PackageWidthCm", "PackageHeightCm", "PackagingSpecsConfirmed", "IsActive"],
        [typeof(ProductImage)] = ["ProductId", "DisplayOrder"],
        [typeof(Inventory)] = ["ProductId", "QuantityAvailable", "QuantityReserved", "LastUpdated"],
        [typeof(Voucher)] = ["Code", "DiscountType", "DiscountValue", "MinOrderAmount", "MaxDiscountAmount", "StartDate", "EndDate", "UsageLimit", "UsedCount", "IsActive"],
        [typeof(Order)] = ["OrderCode", "VoucherId", "SubTotal", "DiscountAmount", "ShippingFee", "TotalAmount", "OrderStatus", "PaymentStatus"],
        [typeof(Delivery)] = ["OrderId", "DeliveryCode", "ShipperUnit", "ShippingCost", "DeliveryStatus", "ShippedAt", "DeliveredAt"],
        [typeof(WorkshopSession)] = ["WorkshopId", "SessionDate", "StartTime", "EndTime", "Location", "Capacity", "BookedSlots", "SessionStatus"],
        [typeof(WorkshopBooking)] = ["SessionId", "BookingCode", "TicketCount", "TotalAmount", "DepositAmount", "RemainingAmount", "BookingStatus", "HoldExpiresAt", "CheckedInAt", "CreatedAt"],
        [typeof(Payment)] = ["OrderId", "BookingId", "TransactionCode", "PaymentType", "PaymentMethod", "Amount", "PaymentStatus", "PaidAt"],
        [typeof(Refund)] = ["OrderId", "BookingId", "TransactionCode", "RefundAmount", "RefundStatus", "RequestedAt", "ProcessedAt"],
        [typeof(CustomOrderRequest)] = ["ProductId", "Quantity", "Status", "RequestedCompletionDate", "ProductionStartedAt", "CompletedAt"],
        [typeof(CustomQuotation)] = ["CustomOrderRequestId", "CurrentRevision", "Status", "ExpiresAt"],
        [typeof(CustomQuotationRevision)] = ["CustomQuotationId", "Version", "UnitPrice", "Quantity", "TotalAmount", "DepositAmount", "CompletionDate", "ExpiresAt", "CreatedBy", "Status"],
        [typeof(CustomOrderCheckout)] = ["CustomQuotationId", "QuotationVersion", "OrderId"],
        [typeof(GatewayPaymentIntent)] = ["OrderId", "Amount", "Currency", "TransactionCode", "Status", "ReconciliationStatus", "EvidenceReference"],
        [typeof(GatewayRefundAttempt)] = ["RefundId", "PaymentId", "Amount", "Status", "ProviderTransactionCode"],
        [typeof(ShippingQuote)] = ["Amount", "ExpiresAt", "OrderId", "UsedAt"]
    };

    public static List<AuditLog> Capture(ChangeTracker tracker, IAuditActor? actor)
    {
        var result = new List<AuditLog>();
        foreach (var entry in tracker.Entries<AuditLog>())
            if (entry.State is EntityState.Modified or EntityState.Deleted)
                throw new InvalidOperationException("AuditLog rows cannot be updated or deleted through the DbContext.");

        foreach (var entry in tracker.Entries().Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted))
        {
            if (!Fields.TryGetValue(entry.Entity.GetType(), out var fields)) continue;
            var oldValues = entry.State == EntityState.Added ? new Dictionary<string, object?>() : Values(entry, fields, false);
            var newValues = entry.State == EntityState.Deleted ? new Dictionary<string, object?>() : Values(entry, fields, true);
            result.Add(new AuditLog
            {
                EntityType = entry.Metadata.ClrType.Name,
                EntityId = Convert.ToString(entry.Property("Id").CurrentValue ?? entry.Property("Id").OriginalValue) ?? "",
                Action = entry.State.ToString(),
                OldValuesJson = JsonSerializer.Serialize(oldValues, JsonOptions),
                NewValuesJson = JsonSerializer.Serialize(newValues, JsonOptions),
                OccurredAtUtc = DateTime.UtcNow,
                ActorId = actor?.ActorId,
                ActorRole = actor?.Role,
                TraceId = actor?.TraceId
            });
        }
        return result;
    }

    // Bulk ExecuteUpdate does not create ChangeTracker entries. Capture both
    // materialized snapshots explicitly so the audit row is written by the
    // caller's SaveChanges/transaction, not by a second connection or commit.
    public static void EnsureConfigured<TEntity>()
    {
        if (!Fields.ContainsKey(typeof(TEntity)))
            throw new InvalidOperationException($"Auditing is not configured for {typeof(TEntity).Name}.");
    }

    public static AuditLog FromSnapshots<TEntity>(TEntity oldValue, TEntity newValue, IAuditActor? actor)
    {
        if (!Fields.TryGetValue(typeof(TEntity), out var fields))
            throw new InvalidOperationException($"Auditing is not configured for {typeof(TEntity).Name}.");

        var oldValues = fields.ToDictionary(f => f, f => typeof(TEntity).GetProperty(f)?.GetValue(oldValue));
        var newValues = fields.ToDictionary(f => f, f => typeof(TEntity).GetProperty(f)?.GetValue(newValue));
        var id = typeof(TEntity).GetProperty("Id")?.GetValue(newValue ?? oldValue);
        return new AuditLog
        {
            EntityType = typeof(TEntity).Name,
            EntityId = Convert.ToString(id) ?? "",
            Action = EntityState.Modified.ToString(),
            OldValuesJson = JsonSerializer.Serialize(oldValues, JsonOptions),
            NewValuesJson = JsonSerializer.Serialize(newValues, JsonOptions),
            OccurredAtUtc = DateTime.UtcNow,
            ActorId = actor?.ActorId,
            ActorRole = actor?.Role,
            TraceId = actor?.TraceId
        };
    }

    private static Dictionary<string, object?> Values(EntityEntry entry, IEnumerable<string> fields, bool current)
        => fields.ToDictionary(f => f, f => (current ? entry.Property(f).CurrentValue : entry.Property(f).OriginalValue));
}
