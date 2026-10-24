using System.ComponentModel.DataAnnotations;
using System.Data;
using HoaSap.DAL;
using HoaSap.Domain;
using HoaSap.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace HoaSap.BLL.Commerce;

public sealed record OrderPaymentSummary(Guid OrderId, decimal TotalAmount, decimal PaidAmount, decimal PendingAmount, decimal RemainingAmount, decimal MinimumNextPayment, bool CanPay, string? PaymentUrl);
public sealed class PaymentReconciliationInput
{
    [Required, MaxLength(20)] public string Outcome { get; set; } = "";
    [MaxLength(100)] public string TransactionCode { get; set; } = "";
    [MaxLength(500)] public string EvidenceReference { get; set; } = "";
}
public sealed record PaymentReconciliationDto(Guid Id, Guid OrderId, decimal Amount, string Status, string ReconciliationStatus, string TransactionCode, string EvidenceReference);

public sealed class GatewayReconciliationService(HoaSapDbContext db, IPaymentGateway? gateway = null)
{
    public async Task<OrderPaymentSummary> BalanceAsync(Guid orderId, Guid ownerId, CancellationToken ct = default)
    {
        var order = await db.Orders.AsNoTracking().SingleOrDefaultAsync(x => x.Id == orderId && x.CustomerId == ownerId, ct) ?? throw new BusinessException(404, "Không tìm thấy đơn.");
        var paid = (await db.Payments.AsNoTracking().Where(x => x.OrderId == orderId && x.PaymentStatus == "Paid").Select(x => x.Amount).ToListAsync(ct)).Sum();
        var pending = (await db.Payments.AsNoTracking().Where(x => x.OrderId == orderId && x.PaymentStatus == "Pending").Select(x => x.Amount).ToListAsync(ct)).Sum();
        var held = await db.GatewayPaymentIntents.AnyAsync(x => x.OrderId == orderId && (x.Status == "Pending" || x.Status == "Unknown" || x.Status == "Accepted" || x.ReconciliationStatus == "ReconciliationRequired"), ct);
        var remaining = Math.Max(0, order.TotalAmount - paid);
        var minimum = remaining;
        var custom = await db.Set<CustomOrderCheckout>().AsNoTracking().SingleOrDefaultAsync(x => x.OrderId == orderId, ct);
        if (paid == 0 && custom is not null) minimum = Math.Min(remaining, await db.Set<CustomQuotationRevision>().Where(x => x.CustomQuotationId == custom.CustomQuotationId && x.Version == custom.QuotationVersion).Select(x => x.DepositAmount).SingleAsync(ct));
        if (minimum == 0) minimum = remaining;
        var paymentUrl = await db.GatewayPaymentIntents.AsNoTracking().Where(x => x.OrderId == orderId && x.Status == "Accepted").OrderByDescending(x => x.CreatedAtUtc).Select(x => x.RedirectUrl).FirstOrDefaultAsync(ct);
        return new(orderId, order.TotalAmount, paid, pending, remaining, minimum, !held && remaining > 0 && order.OrderStatus is "Confirmed" or "Processing" or "Delivering", paymentUrl);
    }

    public async Task<IReadOnlyList<PaymentReconciliationDto>> ListAsync(CancellationToken ct = default) => await db.GatewayPaymentIntents.AsNoTracking()
        .Where(x => x.Status == "Unknown" || x.Status == "Pending" || x.ReconciliationStatus == "ReconciliationRequired" || x.ReconciliationStatus == "RefundRequired")
        .OrderBy(x => x.CreatedAtUtc).Take(200).Select(x => new PaymentReconciliationDto(x.Id, x.OrderId, x.Amount, x.Status, x.ReconciliationStatus, x.TransactionCode, x.EvidenceReference)).ToListAsync(ct);

    /// <summary>MoMo outcomes are queried server-side before recording; non-query providers require Admin evidence.</summary>
    public async Task<PaymentReconciliationDto> ReconcileAsync(Guid id, PaymentReconciliationInput input, CancellationToken ct = default)
    {
        var providerMethod = gateway?.PaymentMethod ?? "GenericSignedGateway";
        if (input is null || input.Outcome is not ("paid" or "not-charged")) throw new BusinessException(400, "Cần kết quả paid hoặc not-charged.");
        var code = input.TransactionCode?.Trim() ?? "";
        var evidenceReference = input.EvidenceReference?.Trim() ?? "";
        GatewayPaymentIntent? queriedIntent = null;
        if (providerMethod == "MoMoSandbox")
        {
            if (gateway is null) throw new BusinessException(503, "Không thể xác minh kết quả MoMo sandbox.");
            queriedIntent = await db.GatewayPaymentIntents.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new BusinessException(404, "Không tìm thấy giao dịch.");
            GatewayPaymentQueryResult result;
            try { result = await gateway.QueryAsync(queriedIntent.ProviderKey, queriedIntent.Amount, ct); }
            catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or TaskCanceledException or NotSupportedException)
            { throw new BusinessException(502, "Không thể xác minh kết quả MoMo sandbox; giao dịch vẫn cần đối soát."); }
            var suffix = queriedIntent.ProviderKey.StartsWith("intent:", StringComparison.Ordinal) ? queriedIntent.ProviderKey["intent:".Length..] : "";
            var expectedOrderId = suffix.Length == 32 ? "I" + suffix : "";
            if (string.IsNullOrEmpty(expectedOrderId) || result.ProviderOrderId != expectedOrderId || result.Amount != queriedIntent.Amount || string.IsNullOrWhiteSpace(result.RequestId))
                throw new BusinessException(502, "MoMo trả về kết quả không khớp giao dịch nội bộ.");
            if (result.Outcome != input.Outcome)
                throw new BusinessException(409, "Kết quả MoMo sandbox mới nhất chưa xác nhận lựa chọn ghi sổ.");
            if (input.Outcome == "paid" && (result.ResultCode != 0 || !long.TryParse(result.TransactionCode, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var transId) || transId <= 0))
                throw new BusinessException(409, "MoMo chưa xác nhận giao dịch đã thu bằng transId hợp lệ.");
            code = input.Outcome == "paid" ? result.TransactionCode : "";
            evidenceReference = $"MoMo sandbox query requestId={result.RequestId} resultCode={result.ResultCode} orderId={result.ProviderOrderId}";
        }
        else if (string.IsNullOrWhiteSpace(evidenceReference) || evidenceReference.Length > 500)
            throw new BusinessException(400, "Cần mã bằng chứng đối soát không chứa dữ liệu cá nhân.");
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var intent = await db.GatewayPaymentIntents.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new BusinessException(404, "Không tìm thấy giao dịch.");
        if (queriedIntent is not null && (intent.ProviderKey != queriedIntent.ProviderKey || intent.Amount != queriedIntent.Amount)) throw new BusinessException(409, "Giao dịch đã thay đổi trong lúc đối soát; hãy truy vấn lại.");
        if (!string.IsNullOrEmpty(intent.TransactionCode) && intent.TransactionCode != code && input.Outcome == "paid" && providerMethod != "MoMoSandbox") throw new BusinessException(409, "Mã giao dịch không khớp biên nhận nội bộ.");
        if (intent.Status == "Paid")
        {
            if (input.Outcome != "paid" || code != intent.TransactionCode) throw new BusinessException(409, "Không được đảo ngược khoản đã thu.");
            await tx.CommitAsync(ct); return Map(intent);
        }
        if (intent.Status is not ("Unknown" or "Pending" or "Accepted" or "Canceled") && intent.ReconciliationStatus != "ReconciliationRequired") throw new BusinessException(409, "Giao dịch không cần đối soát.");
        var payment = string.IsNullOrEmpty(intent.TransactionCode) ? null : await db.Payments.SingleOrDefaultAsync(x => x.OrderId == intent.OrderId && x.TransactionCode == intent.TransactionCode, ct);
        if (payment?.PaymentStatus == "Paid") throw new BusinessException(409, "Khoản thu đã ghi nhận, không thể đảo ngược.");
        intent.EvidenceReference = evidenceReference; intent.UpdatedAtUtc = DateTime.UtcNow;
        if (input.Outcome == "not-charged")
        {
            intent.Status = "Rejected"; intent.ReconciliationStatus = "ResolvedNotCharged";
            if (payment is not null) payment.PaymentStatus = "Canceled";
        }
        else
        {
            if (code.Length is 0 or > 100) throw new BusinessException(400, "Cần mã giao dịch nhà cung cấp, tối đa 100 ký tự.");
            if (providerMethod == "MoMoSandbox" && (!long.TryParse(code, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var transId) || transId <= 0)) throw new BusinessException(400, "Vui lòng nhập MoMo transId số từ trang đối soát sandbox, không phải mã đơn nội bộ.");
            var paymentId = payment?.Id ?? Guid.Empty;
            if (await db.Payments.AnyAsync(x => x.TransactionCode == code && x.Id != paymentId, ct)) throw new BusinessException(409, "Mã giao dịch đã được sử dụng.");
            var order = await db.Orders.SingleAsync(x => x.Id == intent.OrderId, ct);
            var paid = (await db.Payments.Where(x => x.OrderId == order.Id && x.PaymentStatus == "Paid").Select(x => x.Amount).ToListAsync(ct)).Sum();
            if (paid + intent.Amount > order.TotalAmount) throw new BusinessException(409, "Thu vượt tổng tiền; giữ giao dịch chờ đối soát ngoại lệ, không ghi đè sổ.");
            payment ??= new Payment { OrderId = order.Id, Amount = intent.Amount, TransactionCode = code, PaymentType = "Gateway", PaymentMethod = providerMethod };
            if (db.Entry(payment).State == EntityState.Detached) db.Add(payment);
            payment.TransactionCode = code; payment.PaymentMethod = providerMethod; payment.PaymentStatus = "Paid"; payment.PaidAt = DateTime.UtcNow;
            intent.TransactionCode = code; intent.Status = "Paid";
            intent.ReconciliationStatus = order.OrderStatus is "Cancelled" or "Expired" ? "RefundRequired" : "ResolvedPaid";
            order.PaymentStatus = paid + intent.Amount >= order.TotalAmount ? "Paid" : "PartiallyPaid";
        }
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return Map(intent);
    }
    private static PaymentReconciliationDto Map(GatewayPaymentIntent x) => new(x.Id, x.OrderId, x.Amount, x.Status, x.ReconciliationStatus, x.TransactionCode, x.EvidenceReference);
}
