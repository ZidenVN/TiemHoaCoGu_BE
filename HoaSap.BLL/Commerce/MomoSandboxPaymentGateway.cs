using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace HoaSap.BLL.Commerce;

/// <summary>MoMo wallet `captureWallet` adapter. The host is deliberately fixed to MoMo's Test environment.</summary>
public sealed class MomoSandboxPaymentGateway(HttpClient http, IOptions<CommerceGatewayOptions> options) : IPaymentGateway, IDisposable
{
    private const string CreateUrl = "https://test-payment.momo.vn/v2/gateway/api/create";
    private const string RefundUrl = "https://test-payment.momo.vn/v2/gateway/api/refund";
    private const string QueryUrl = "https://test-payment.momo.vn/v2/gateway/api/query";
    private const string PaymentMethodName = "MoMoSandbox";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };
    private readonly CommerceGatewayOptions cfg = options.Value;
    public string PaymentMethod => PaymentMethodName;
    public void Dispose() => http.Dispose();

    public async Task<GatewayChargeResult> ChargeAsync(GatewayChargeRequest request, CancellationToken ct = default)
    {
        cfg.Validate();
        if (cfg.Provider != "MomoSandbox") throw new InvalidOperationException("MoMo adapter is not enabled for this environment.");
        if (request.Currency != "VND" || request.Amount < 1_000m || request.Amount > 50_000_000m || decimal.Truncate(request.Amount) != request.Amount)
            throw new InvalidOperationException("MoMo sandbox charge requires a whole-VND amount from 1,000 to 50,000,000.");
        var suffix = IntentSuffix(request.ProviderKey);
        var orderId = "I" + suffix;
        var requestId = "R" + suffix;
        const string orderInfo = "HoaSap order payment";
        const string extraData = "";
        const string requestType = "captureWallet";
        var amount = decimal.ToInt64(request.Amount).ToString(CultureInfo.InvariantCulture);
        var signatureData = $"accessKey={cfg.MomoAccessKey}&amount={amount}&extraData={extraData}&ipnUrl={cfg.MomoIpnUrl}&orderId={orderId}&orderInfo={orderInfo}&partnerCode={cfg.MomoPartnerCode}&redirectUrl={request.ReturnUrl}&requestId={requestId}&requestType={requestType}";
        var body = JsonSerializer.Serialize(new
        {
            partnerCode = cfg.MomoPartnerCode,
            requestId,
            orderId,
            amount = decimal.ToInt64(request.Amount),
            orderInfo,
            redirectUrl = request.ReturnUrl,
            ipnUrl = cfg.MomoIpnUrl,
            requestType,
            extraData,
            lang = "vi",
            signature = Sign(signatureData)
        }, JsonOptions);
        using var response = await SendAsync(CreateUrl, body, ct);
        var raw = await response.Content.ReadAsStringAsync(ct);
        MomoCreateResponse? result;
        try { result = JsonSerializer.Deserialize<MomoCreateResponse>(raw, JsonOptions); }
        catch (JsonException ex) { throw new InvalidOperationException("MoMo sandbox returned an invalid create response.", ex); }
        if (result is null) throw new InvalidOperationException("MoMo sandbox returned an empty create response.");
        if (result.PartnerCode != cfg.MomoPartnerCode || result.RequestId != requestId || result.OrderId != orderId || result.Amount != decimal.ToInt64(request.Amount))
            throw new InvalidOperationException("MoMo sandbox create response did not match the request.");
        var responseData = $"accessKey={cfg.MomoAccessKey}&amount={result.Amount.ToString(CultureInfo.InvariantCulture)}&message={result.Message}&orderId={result.OrderId}&partnerCode={result.PartnerCode}&payUrl={result.PayUrl}&requestId={result.RequestId}&responseTime={result.ResponseTime.ToString(CultureInfo.InvariantCulture)}&resultCode={result.ResultCode.ToString(CultureInfo.InvariantCulture)}";
        if (!ValidSignature(responseData, result.Signature)) throw new InvalidOperationException("MoMo sandbox create response signature was invalid.");
        if (result.ResultCode == 9000) throw new InvalidOperationException("MoMo authorized the payment without confirming capture; query/reconcile before any retry.");
        if (result.ResultCode != 0)
        {
            if (!response.IsSuccessStatusCode && (int)response.StatusCode >= 500) throw new HttpRequestException("MoMo sandbox returned a server error.");
            if (!IsFinalFailure(result.ResultCode)) throw new InvalidOperationException("MoMo returned a pending or unclassified result; query/reconcile before any retry.");
            return new(false, orderId, null, $"resultCode={result.ResultCode.ToString(CultureInfo.InvariantCulture)}");
        }
        if (!response.IsSuccessStatusCode) throw new HttpRequestException("MoMo sandbox returned an unconfirmed create response.");
        if (!Uri.TryCreate(result.PayUrl, UriKind.Absolute, out var payUri) || payUri.Scheme != Uri.UriSchemeHttps || !payUri.Host.Equals("test-payment.momo.vn", StringComparison.OrdinalIgnoreCase) || !string.IsNullOrEmpty(payUri.UserInfo))
            throw new InvalidOperationException("MoMo sandbox payment URL did not match the test host.");
        return new(true, orderId, result.PayUrl, null);
    }

    public async Task<GatewayRefundResult> RefundAsync(GatewayRefundRequest request, CancellationToken ct = default)
    {
        cfg.Validate();
        if (cfg.Provider != "MomoSandbox") throw new InvalidOperationException("MoMo adapter is not enabled for this environment.");
        if (request.Currency != "VND" || request.Amount < 1_000m || request.Amount > 50_000_000m || decimal.Truncate(request.Amount) != request.Amount)
            throw new InvalidOperationException("MoMo sandbox refund requires a whole-VND amount from 1,000 to 50,000,000.");
        if (!long.TryParse(request.OriginalTransactionCode, NumberStyles.None, CultureInfo.InvariantCulture, out var originalTransId) || originalTransId <= 0)
            throw new InvalidOperationException("MoMo refund requires the original MoMo transId.");
        var refundId = request.RefundId.ToString("N");
        var orderId = "F" + refundId;
        var requestId = "R" + refundId;
        const string description = "HoaSap sandbox refund";
        var amount = decimal.ToInt64(request.Amount).ToString(CultureInfo.InvariantCulture);
        var transId = originalTransId.ToString(CultureInfo.InvariantCulture);
        var signatureData = $"accessKey={cfg.MomoAccessKey}&amount={amount}&description={description}&orderId={orderId}&partnerCode={cfg.MomoPartnerCode}&requestId={requestId}&transId={transId}";
        var body = JsonSerializer.Serialize(new
        {
            partnerCode = cfg.MomoPartnerCode,
            orderId,
            requestId,
            amount = decimal.ToInt64(request.Amount),
            transId = originalTransId,
            lang = "vi",
            description,
            signature = Sign(signatureData)
        }, JsonOptions);
        using var response = await SendAsync(RefundUrl, body, ct);
        // MoMo's documented refund response has no response-signature field; rely on TLS and strict request/amount/partner correlation.
        var raw = await response.Content.ReadAsStringAsync(ct);
        MomoRefundResponse? result;
        try { result = JsonSerializer.Deserialize<MomoRefundResponse>(raw, JsonOptions); }
        catch (JsonException ex) { throw new InvalidOperationException("MoMo sandbox returned an invalid refund response.", ex); }
        if (result is null || result.PartnerCode != cfg.MomoPartnerCode || result.OrderId != orderId || result.RequestId != requestId || result.Amount != decimal.ToInt64(request.Amount) || result.ResponseTime <= 0)
            throw new InvalidOperationException("MoMo sandbox refund response did not match the request.");
        if (!response.IsSuccessStatusCode && (int)response.StatusCode >= 500) throw new HttpRequestException("MoMo sandbox returned a refund server error.");
        if (!response.IsSuccessStatusCode || result.ResultCode != 0) return new(false, orderId, $"resultCode={result.ResultCode.ToString(CultureInfo.InvariantCulture)}");
        if (result.TransId <= 0) throw new InvalidOperationException("MoMo confirmed a refund without a valid refund transaction ID; reconcile before any retry.");
        return new(true, result.TransId.ToString(CultureInfo.InvariantCulture), null);
    }

    public async Task<GatewayPaymentQueryResult> QueryAsync(string providerKey, decimal expectedAmount, CancellationToken ct = default)
    {
        cfg.Validate();
        if (cfg.Provider != "MomoSandbox") throw new InvalidOperationException("MoMo adapter is not enabled for this environment.");
        if (expectedAmount < 1_000m || expectedAmount > 50_000_000m || decimal.Truncate(expectedAmount) != expectedAmount) throw new InvalidOperationException("MoMo sandbox query requires the original whole-VND amount.");
        var orderId = "I" + IntentSuffix(providerKey);
        var requestId = "Q" + Guid.NewGuid().ToString("N");
        const string lang = "vi";
        var signatureData = $"accessKey={cfg.MomoAccessKey}&orderId={orderId}&partnerCode={cfg.MomoPartnerCode}&requestId={requestId}";
        var body = JsonSerializer.Serialize(new { partnerCode = cfg.MomoPartnerCode, requestId, orderId, lang, signature = Sign(signatureData) }, JsonOptions);
        using var response = await SendAsync(QueryUrl, body, ct);
        // MoMo's documented query response has no response-signature field; rely on TLS and strict request/order/amount correlation.
        var raw = await response.Content.ReadAsStringAsync(ct);
        MomoQueryResponse? result;
        try { result = JsonSerializer.Deserialize<MomoQueryResponse>(raw, JsonOptions); }
        catch (JsonException ex) { throw new InvalidOperationException("MoMo sandbox returned an invalid query response.", ex); }
        if (result is null || result.PartnerCode != cfg.MomoPartnerCode || result.OrderId != orderId || result.RequestId != requestId || result.ResponseTime <= 0)
            throw new InvalidOperationException("MoMo sandbox query response did not match the request.");
        if (!response.IsSuccessStatusCode) throw new HttpRequestException("MoMo sandbox query was not confirmed.");
        var amountMatches = result.Amount == decimal.ToInt64(expectedAmount);
        var transId = result.TransId > 0 ? result.TransId.ToString(CultureInfo.InvariantCulture) : "";
        var outcome = !amountMatches ? "unknown" : result.ResultCode switch
        {
            0 when result.TransId > 0 => "paid",
            9000 => "authorized",
            1000 or 7000 or 7002 => "pending",
            _ when IsFinalFailure(result.ResultCode) => "not-charged",
            _ => "unknown"
        };
        return new(outcome, result.ResultCode, result.OrderId, result.RequestId, result.Amount, transId, result.Message, result.ResponseTime);
    }

    public bool VerifyWebhook(ReadOnlySpan<byte> payload, string? signature, string? timestamp)
    {
        try
        {
            cfg.Validate();
            var ipn = JsonSerializer.Deserialize<MomoIpn>(payload, JsonOptions);
            if (ipn is null || ipn.PartnerCode != cfg.MomoPartnerCode || ipn.Amount <= 0 || ipn.ResultCode == 0 && ipn.TransId <= 0 || ipn.ResponseTime <= 0 || string.IsNullOrWhiteSpace(ipn.RequestId) || string.IsNullOrWhiteSpace(ipn.OrderId)) return false;
            var data = $"accessKey={cfg.MomoAccessKey}&amount={ipn.Amount.ToString(CultureInfo.InvariantCulture)}&extraData={ipn.ExtraData}&message={ipn.Message}&orderId={ipn.OrderId}&orderInfo={ipn.OrderInfo}&orderType={ipn.OrderType}&partnerCode={ipn.PartnerCode}&payType={ipn.PayType}&requestId={ipn.RequestId}&responseTime={ipn.ResponseTime.ToString(CultureInfo.InvariantCulture)}&resultCode={ipn.ResultCode.ToString(CultureInfo.InvariantCulture)}&transId={ipn.TransId.ToString(CultureInfo.InvariantCulture)}";
            return ValidSignature(data, ipn.Signature);
        }
        catch { return false; }
    }

    public GatewayWebhookRequest ParseWebhook(ReadOnlySpan<byte> payload)
    {
        var ipn = JsonSerializer.Deserialize<MomoIpn>(payload, JsonOptions) ?? throw new JsonException("MoMo IPN payload is invalid.");
        var suffix = ipn.OrderId.StartsWith('I') ? ipn.OrderId[1..] : "";
        if (suffix.Length != 32 || !Guid.TryParseExact(suffix, "N", out _)) throw new JsonException("MoMo orderId does not map to a local payment intent.");
        return new GatewayWebhookRequest
        {
            ProviderKey = "intent:" + suffix,
            ProviderOrderId = ipn.OrderId,
            ProviderTransactionId = ipn.TransId > 0 ? ipn.TransId.ToString(CultureInfo.InvariantCulture) : "",
            Amount = ipn.Amount,
            Currency = "VND",
            Status = ipn.ResultCode == 0 ? "paid" : ipn.ResultCode == 9000 ? "authorized" : IsFinalFailure(ipn.ResultCode) ? "canceled" : "pending"
        };
    }

    private async Task<HttpResponseMessage> SendAsync(string url, string body, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(cfg.TimeoutSeconds));
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        return await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
    }

    private string Sign(string data) => Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(cfg.MomoSecretKey), Encoding.UTF8.GetBytes(data))).ToLowerInvariant();
    private static bool IsFinalFailure(int resultCode) => resultCode is >= 1001 and <= 1007 or 1017;
    private bool ValidSignature(string data, string? signature)
    {
        if (string.IsNullOrWhiteSpace(signature) || signature.Length != 64) return false;
        var expected = Sign(data);
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(signature.Trim().ToLowerInvariant()));
    }

    private static string IntentSuffix(string providerKey)
    {
        const string prefix = "intent:";
        var suffix = providerKey.StartsWith(prefix, StringComparison.Ordinal) ? providerKey[prefix.Length..] : "";
        if (suffix.Length != 32 || !Guid.TryParseExact(suffix, "N", out _)) throw new InvalidOperationException("MoMo payment intent key is invalid.");
        return suffix;
    }

    private sealed class MomoCreateResponse
    {
        [JsonPropertyName("partnerCode")] public string PartnerCode { get; set; } = "";
        [JsonPropertyName("requestId")] public string RequestId { get; set; } = "";
        [JsonPropertyName("orderId")] public string OrderId { get; set; } = "";
        [JsonPropertyName("amount")] public long Amount { get; set; }
        [JsonPropertyName("responseTime")] public long ResponseTime { get; set; }
        [JsonPropertyName("message")] public string Message { get; set; } = "";
        [JsonPropertyName("resultCode")] public int ResultCode { get; set; }
        [JsonPropertyName("payUrl")] public string PayUrl { get; set; } = "";
        [JsonPropertyName("signature")] public string Signature { get; set; } = "";
    }
    private sealed class MomoRefundResponse
    {
        [JsonPropertyName("partnerCode")] public string PartnerCode { get; set; } = "";
        [JsonPropertyName("orderId")] public string OrderId { get; set; } = "";
        [JsonPropertyName("requestId")] public string RequestId { get; set; } = "";
        [JsonPropertyName("amount")] public long Amount { get; set; }
        [JsonPropertyName("transId")] public long TransId { get; set; }
        [JsonPropertyName("resultCode")] public int ResultCode { get; set; }
        [JsonPropertyName("responseTime")] public long ResponseTime { get; set; }
        [JsonPropertyName("message")] public string Message { get; set; } = "";
    }
    private sealed class MomoQueryResponse
    {
        [JsonPropertyName("partnerCode")] public string PartnerCode { get; set; } = "";
        [JsonPropertyName("requestId")] public string RequestId { get; set; } = "";
        [JsonPropertyName("orderId")] public string OrderId { get; set; } = "";
        [JsonPropertyName("amount")] public long Amount { get; set; }
        [JsonPropertyName("transId")] public long TransId { get; set; }
        [JsonPropertyName("resultCode")] public int ResultCode { get; set; }
        [JsonPropertyName("message")] public string Message { get; set; } = "";
        [JsonPropertyName("responseTime")] public long ResponseTime { get; set; }
    }
    private sealed class MomoIpn
    {
        [JsonPropertyName("partnerCode")] public string PartnerCode { get; set; } = "";
        [JsonPropertyName("orderId")] public string OrderId { get; set; } = "";
        [JsonPropertyName("requestId")] public string RequestId { get; set; } = "";
        [JsonPropertyName("amount")] public long Amount { get; set; }
        [JsonPropertyName("transId")] public long TransId { get; set; }
        [JsonPropertyName("responseTime")] public long ResponseTime { get; set; }
        [JsonPropertyName("resultCode")] public int ResultCode { get; set; }
        [JsonPropertyName("message")] public string Message { get; set; } = "";
        [JsonPropertyName("extraData")] public string ExtraData { get; set; } = "";
        [JsonPropertyName("orderInfo")] public string OrderInfo { get; set; } = "";
        [JsonPropertyName("orderType")] public string OrderType { get; set; } = "";
        [JsonPropertyName("payType")] public string PayType { get; set; } = "";
        [JsonPropertyName("signature")] public string Signature { get; set; } = "";
    }
}
