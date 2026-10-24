using System.Data;
using HoaSap.DAL;
using HoaSap.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HoaSap.BLL.Commerce;

public sealed class GatewayRefundService(RefundService refunds, IPaymentGateway gateway, HoaSapDbContext db, IOptions<CommerceGatewayOptions> options)
{
    public async Task<RefundDto> ProcessAsync(Guid refundId, string originalTransactionCode, CancellationToken ct = default)
    {
        try { options.Value.Validate(); } catch (InvalidOperationException) { throw new BusinessException(503, "Chưa cấu hình cổng hoàn tiền."); }
        GatewayRefundAttempt attempt;
        string sourceCode;
        await using (var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct))
        {
            var refund = await db.Refunds.SingleOrDefaultAsync(x => x.Id == refundId, ct) ?? throw new BusinessException(404, "Không tìm thấy yêu cầu hoàn tiền.");
            if (refund.RefundStatus == "Processed") { await tx.CommitAsync(ct); return await refunds.GetForGatewayAsync(refundId, ct); }
            if (refund.RefundStatus != "Approved") throw new BusinessException(409, "Hoàn tiền phải được Admin duyệt và chưa gửi đi.");
            if (refund.OrderId is null || string.IsNullOrWhiteSpace(originalTransactionCode)) throw new BusinessException(400, "Cần đơn hàng và giao dịch gateway gốc.");
            sourceCode = originalTransactionCode.Trim();
            var payment = await db.Payments.SingleOrDefaultAsync(x => x.OrderId == refund.OrderId && x.TransactionCode == sourceCode && x.PaymentStatus == "Paid" && x.PaymentMethod == gateway.PaymentMethod, ct)
                ?? throw new BusinessException(400, "Giao dịch gốc không phải khoản thu gateway của đơn này.");
            if (gateway.PaymentMethod == "MoMoSandbox" && (refund.RefundAmount < 1_000m || refund.RefundAmount > 50_000_000m || decimal.Truncate(refund.RefundAmount) != refund.RefundAmount || !long.TryParse(sourceCode, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var transId) || transId <= 0))
                throw new BusinessException(400, "Hoàn MoMo sandbox cần MoMo transId và số tiền nguyên VND từ 1.000 đến 50.000.000.");
            if (await db.Set<GatewayRefundAttempt>().AnyAsync(x => x.RefundId == refundId, ct)) throw new BusinessException(409, "Yêu cầu đã gửi gateway, cần đối soát trước khi xử lý tiếp.");
            var allocated = (await db.Set<GatewayRefundAttempt>().Where(x => x.PaymentId == payment.Id).Select(x => x.Amount).ToListAsync(ct)).Sum();
            if (refund.RefundAmount > payment.Amount - allocated) throw new BusinessException(400, "Tổng hoàn trên giao dịch gốc vượt khoản đã thu.");
            attempt = new GatewayRefundAttempt { RefundId = refund.Id, PaymentId = payment.Id, Amount = refund.RefundAmount };
            db.Add(attempt);
            refund.RefundStatus = "Processing";
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        GatewayRefundResult response;
        try { response = await gateway.RefundAsync(new GatewayRefundRequest(refundId, attempt.Amount, "VND", sourceCode), ct); }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            attempt.Status = "Unknown";
            await db.SaveChangesAsync(CancellationToken.None);
            throw new BusinessException(503, "Chưa rõ kết quả hoàn tiền. Cần đối soát, không gửi lại yêu cầu.");
        }
        if (!response.Accepted || string.IsNullOrWhiteSpace(response.TransactionCode) || response.TransactionCode.Length > 100)
        {
            attempt.Status = "Unknown";
            await db.SaveChangesAsync(ct);
            throw new BusinessException(502, "Phản hồi hoàn tiền chưa xác nhận; cần Admin đối soát.");
        }
        // Persist the provider receipt first so a ledger failure can be reconciled without resending money.
        attempt.ProviderTransactionCode = response.TransactionCode.Trim(); attempt.Status = "ProviderConfirmed";
        await db.SaveChangesAsync(ct);
        var result = await refunds.CompleteGatewayAsync(refundId, attempt.ProviderTransactionCode, ct);
        attempt.Status = "Completed";
        await db.SaveChangesAsync(ct);
        return result;
    }

    /// <summary>Admin records a verified provider receipt. This endpoint never sends another refund.</summary>
    public async Task<RefundDto> ReconcileAsync(Guid refundId, string transactionCode, CancellationToken ct = default)
    {
        var attempt = await db.Set<GatewayRefundAttempt>().SingleOrDefaultAsync(x => x.RefundId == refundId, ct) ?? throw new BusinessException(404, "Không có lần hoàn gateway để đối soát.");
        var receipt = transactionCode?.Trim() ?? "";
        if (receipt.Length is 0 or > 100) throw new BusinessException(400, "Cần mã biên nhận hoàn tiền của nhà cung cấp.");
        if (attempt.Status == "Completed")
        {
            if (attempt.ProviderTransactionCode != receipt) throw new BusinessException(409, "Mã đối soát không khớp biên nhận đã lưu.");
            return await refunds.GetForGatewayAsync(refundId, ct);
        }
        if (attempt.Status is not ("Unknown" or "ProviderConfirmed")) throw new BusinessException(409, "Lần hoàn này chưa ở trạng thái cần đối soát.");
        if (!string.IsNullOrEmpty(attempt.ProviderTransactionCode) && attempt.ProviderTransactionCode != receipt) throw new BusinessException(409, "Mã đối soát không khớp biên nhận đã lưu.");
        var result = await refunds.CompleteGatewayAsync(refundId, receipt, ct);
        attempt.ProviderTransactionCode = receipt; attempt.Status = "Completed";
        await db.SaveChangesAsync(ct);
        return result;
    }
}
