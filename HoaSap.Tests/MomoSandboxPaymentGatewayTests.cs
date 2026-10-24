using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HoaSap.BLL.Commerce;
using HoaSap.DAL;
using HoaSap.Domain;
using HoaSap.Domain.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace HoaSap.Tests;

public sealed class MomoSandboxPaymentGatewayTests
{
    [Fact]
    public async Task Create_uses_fixed_test_endpoint_and_documented_hmac_contract()
    {
        var key = Guid.NewGuid().ToString("N");
        string? url = null;
        string? rawRequest = null;
        var handler = new Handler(async (request, ct) =>
        {
            url = request.RequestUri?.ToString();
            rawRequest = await request.Content!.ReadAsStringAsync(ct);
            using var body = JsonDocument.Parse(rawRequest);
            var root = body.RootElement;
            var orderId = root.GetProperty("orderId").GetString()!;
            var requestId = root.GetProperty("requestId").GetString()!;
            var amount = root.GetProperty("amount").GetInt64();
            var orderInfo = root.GetProperty("orderInfo").GetString()!;
            var redirectUrl = root.GetProperty("redirectUrl").GetString()!;
            var ipnUrl = root.GetProperty("ipnUrl").GetString()!;
            var requestType = root.GetProperty("requestType").GetString()!;
            var extraData = root.GetProperty("extraData").GetString()!;
            var signData = $"accessKey=access-test&amount={amount}&extraData={extraData}&ipnUrl={ipnUrl}&orderId={orderId}&orderInfo={orderInfo}&partnerCode=PARTNER_TEST&redirectUrl={redirectUrl}&requestId={requestId}&requestType={requestType}";
            Assert.Equal(Sign("secret-test", signData), root.GetProperty("signature").GetString());
            Assert.Equal("captureWallet", requestType);
            Assert.Equal(1_000L, amount);
            Assert.Equal("vi", root.GetProperty("lang").GetString());
            var payUrl = "https://test-payment.momo.vn/gateway/pay?t=sandbox-token";
            var responseData = $"accessKey=access-test&amount={amount}&message=Successful.&orderId={orderId}&partnerCode=PARTNER_TEST&payUrl={payUrl}&requestId={requestId}&responseTime=1730000000000&resultCode=0";
            var response = new
            {
                partnerCode = "PARTNER_TEST", requestId, orderId, amount,
                responseTime = 1730000000000L, message = "Successful.", resultCode = 0,
                payUrl, signature = Sign("secret-test", responseData)
            };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(response), Encoding.UTF8, "application/json") };
        });
        using var client = new HttpClient(handler);
        using var gateway = new MomoSandboxPaymentGateway(client, Config(endpoint: "https://api.momo.vn"));
        var result = await gateway.ChargeAsync(new GatewayChargeRequest(Guid.NewGuid(), 1_000m, "VND", "https://shop.example.test/payment-result", "intent:" + key));

        Assert.Equal("https://test-payment.momo.vn/v2/gateway/api/create", url);
        Assert.NotNull(rawRequest);
        Assert.True(result.Accepted);
        Assert.Equal("I" + key, result.TransactionCode);
        Assert.Equal("https://test-payment.momo.vn/gateway/pay?t=sandbox-token", result.RedirectUrl);
    }

    [Theory]
    [InlineData(1000)]
    [InlineData(7000)]
    [InlineData(7002)]
    [InlineData(7777)]
    public async Task Create_pending_or_unclassified_result_is_not_reported_as_a_definite_rejection(int resultCode)
    {
        var handler = new Handler(async (request, ct) =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var root = body.RootElement;
            var orderId = root.GetProperty("orderId").GetString()!;
            var requestId = root.GetProperty("requestId").GetString()!;
            var amount = root.GetProperty("amount").GetInt64();
            const string message = "Processing";
            const string payUrl = "";
            const long responseTime = 1730000000000L;
            var responseData = $"accessKey=access-test&amount={amount}&message={message}&orderId={orderId}&partnerCode=PARTNER_TEST&payUrl={payUrl}&requestId={requestId}&responseTime={responseTime}&resultCode={resultCode}";
            var response = new { partnerCode = "PARTNER_TEST", requestId, orderId, amount, responseTime, message, resultCode, payUrl, signature = Sign("secret-test", responseData) };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(response), Encoding.UTF8, "application/json") };
        });
        using var gateway = NewGateway(handler);

        await Assert.ThrowsAsync<InvalidOperationException>(() => gateway.ChargeAsync(new GatewayChargeRequest(Guid.NewGuid(), 1_000m, "VND", "https://shop.example.test/return", "intent:" + Guid.NewGuid().ToString("N"))));
    }

    [Fact]
    public async Task Refund_uses_original_momo_transid_and_stable_request_identity_on_test_host()
    {
        var refundId = Guid.NewGuid();
        string? url = null;
        string? rawRequest = null;
        var handler = new Handler(async (request, ct) =>
        {
            url = request.RequestUri?.ToString();
            rawRequest = await request.Content!.ReadAsStringAsync(ct);
            using var body = JsonDocument.Parse(rawRequest);
            var root = body.RootElement;
            var orderId = root.GetProperty("orderId").GetString()!;
            var requestId = root.GetProperty("requestId").GetString()!;
            var amount = root.GetProperty("amount").GetInt64();
            var transId = root.GetProperty("transId").GetInt64();
            var description = root.GetProperty("description").GetString()!;
            Assert.Equal("F" + refundId.ToString("N"), orderId);
            Assert.Equal("R" + refundId.ToString("N"), requestId);
            Assert.Equal(99887766L, transId);
            var signData = $"accessKey=access-test&amount={amount}&description={description}&orderId={orderId}&partnerCode=PARTNER_TEST&requestId={requestId}&transId={transId}";
            Assert.Equal(Sign("secret-test", signData), root.GetProperty("signature").GetString());
            var response = new { partnerCode = "PARTNER_TEST", orderId, requestId, amount, transId = 77665544L, resultCode = 0, message = "Successful.", responseTime = 1730000000000L };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(response), Encoding.UTF8, "application/json") };
        });
        using var client = new HttpClient(handler);
        using var gateway = new MomoSandboxPaymentGateway(client, Config());

        var result = await gateway.RefundAsync(new GatewayRefundRequest(refundId, 1_000m, "VND", "99887766"));

        Assert.Equal("https://test-payment.momo.vn/v2/gateway/api/refund", url);
        Assert.NotNull(rawRequest);
        Assert.True(result.Accepted);
        Assert.Equal("77665544", result.TransactionCode);
    }

    [Fact]
    public void Ipns_are_verified_from_body_and_normalized_to_the_intent_and_momo_transid()
    {
        var gateway = NewGateway(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))));
        var key = Guid.NewGuid().ToString("N");
        var payload = IpN(key, amount: 12500, transId: 88112233, resultCode: 0);

        Assert.True(gateway.VerifyWebhook(payload, null, null));
        var normalized = gateway.ParseWebhook(payload);
        Assert.Equal("intent:" + key, normalized.ProviderKey);
        Assert.Equal("I" + key, normalized.ProviderOrderId);
        Assert.Equal("88112233", normalized.ProviderTransactionId);
        Assert.Equal(12500m, normalized.Amount);
        Assert.Equal("paid", normalized.Status);

        var tampered = Encoding.UTF8.GetString(payload).Replace("\"amount\":12500", "\"amount\":12501", StringComparison.Ordinal);
        Assert.False(gateway.VerifyWebhook(Encoding.UTF8.GetBytes(tampered), null, null));
    }

    [Fact]
    public async Task Valid_ipn_settles_an_unknown_create_once_and_late_success_requires_reconciliation()
    {
        await using var store = await Store.CreateAsync();
        var key = Guid.NewGuid().ToString("N");
        var (order, intent) = await store.SeedUnknownAsync(key, canceled: false);
        using var gateway = NewGateway(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))));
        var service = new GatewayPaymentService(store.Db, gateway, Config());
        var ipn = IpN(key, amount: 12500, transId: 88112233, resultCode: 0);

        var first = await service.WebhookAsync(ipn, null, null);
        var replay = await service.WebhookAsync(ipn, null, null);

        Assert.True(first.Accepted);
        Assert.False(first.Replay);
        Assert.True(replay.Replay);
        var payment = await store.Db.Payments.SingleAsync();
        Assert.Equal("88112233", payment.TransactionCode);
        Assert.Equal("MoMoSandbox", payment.PaymentMethod);
        Assert.Equal("Paid", payment.PaymentStatus);
        Assert.Equal("Paid", (await store.Db.Orders.SingleAsync(x => x.Id == order.Id)).PaymentStatus);
        Assert.Equal("Paid", (await store.Db.GatewayPaymentIntents.SingleAsync(x => x.Id == intent.Id)).Status);
    }

    [Fact]
    public async Task Valid_late_ipn_does_not_revive_canceled_order_or_mark_payment_paid()
    {
        await using var store = await Store.CreateAsync();
        var key = Guid.NewGuid().ToString("N");
        var (order, intent) = await store.SeedUnknownAsync(key, canceled: true);
        using var gateway = NewGateway(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))));
        var service = new GatewayPaymentService(store.Db, gateway, Config());

        var result = await service.WebhookAsync(IpN(key, amount: 12500, transId: 88112244, resultCode: 0), null, null);

        Assert.True(result.Accepted);
        Assert.False(result.Replay);
        Assert.Equal("Cancelled", (await store.Db.Orders.SingleAsync(x => x.Id == order.Id)).OrderStatus);
        Assert.Equal("Unpaid", (await store.Db.Orders.SingleAsync(x => x.Id == order.Id)).PaymentStatus);
        Assert.Equal("ReconciliationRequired", (await store.Db.Payments.SingleAsync()).PaymentStatus);
        var saved = await store.Db.GatewayPaymentIntents.SingleAsync(x => x.Id == intent.Id);
        Assert.Equal("ReconciliationRequired", saved.ReconciliationStatus);
        Assert.Equal("88112244", saved.TransactionCode);
    }

    [Theory]
    [InlineData(1000)]
    [InlineData(7000)]
    [InlineData(7002)]
    [InlineData(7777)]
    public async Task Pending_or_unclassified_ipn_stays_held_for_query_reconciliation(int resultCode)
    {
        await using var store = await Store.CreateAsync();
        var key = Guid.NewGuid().ToString("N");
        var (order, intent) = await store.SeedUnknownAsync(key, canceled: false);
        using var gateway = NewGateway(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))));
        var service = new GatewayPaymentService(store.Db, gateway, Config());

        var result = await service.WebhookAsync(IpN(key, amount: 12500, transId: 0, resultCode), null, null);

        Assert.True(result.Accepted);
        Assert.False(result.Replay);
        var saved = await store.Db.GatewayPaymentIntents.SingleAsync(x => x.Id == intent.Id);
        Assert.Equal("Unknown", saved.Status);
        Assert.Equal("ReconciliationRequired", saved.ReconciliationStatus);
        Assert.Empty(await store.Db.Payments.ToListAsync());
        Assert.Equal("Unpaid", (await store.Db.Orders.SingleAsync(x => x.Id == order.Id)).PaymentStatus);
    }

    [Fact]
    public async Task Momo_admin_reconciliation_uses_a_fresh_provider_query_not_client_receipt_or_evidence()
    {
        await using var store = await Store.CreateAsync();
        var key = Guid.NewGuid().ToString("N");
        var (order, intent) = await store.SeedUnknownAsync(key, canceled: false);
        var handler = new Handler(async (request, ct) =>
        {
            Assert.Equal("https://test-payment.momo.vn/v2/gateway/api/query", request.RequestUri?.ToString());
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var root = body.RootElement;
            var response = new { partnerCode = "PARTNER_TEST", orderId = root.GetProperty("orderId").GetString(), requestId = root.GetProperty("requestId").GetString(), amount = 12500L, transId = 88112233L, resultCode = 0, message = "Successful.", responseTime = 1730000000000L };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(response), Encoding.UTF8, "application/json") };
        });
        using var gateway = NewGateway(handler);
        var service = new GatewayReconciliationService(store.Db, gateway);

        var saved = await service.ReconcileAsync(intent.Id, new PaymentReconciliationInput { Outcome = "paid", TransactionCode = "client-forged-id", EvidenceReference = "client-forged-evidence" });

        Assert.Equal("Paid", saved.Status);
        Assert.Equal("88112233", saved.TransactionCode);
        Assert.Contains("MoMo sandbox query", saved.EvidenceReference, StringComparison.Ordinal);
        Assert.DoesNotContain("client-forged", saved.EvidenceReference, StringComparison.Ordinal);
        Assert.Equal("Paid", (await store.Db.Orders.SingleAsync(x => x.Id == order.Id)).PaymentStatus);
    }

    [Fact]
    public async Task Invalid_momo_ipn_configuration_fails_before_any_http_call()
    {
        var calls = 0;
        var handler = new Handler((_, _) => { calls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)); });
        using var client = new HttpClient(handler);
        var invalid = Options.Create(new CommerceGatewayOptions
        {
            Provider = "MomoSandbox", MomoPartnerCode = "PARTNER_TEST", MomoAccessKey = "access-test", MomoSecretKey = "secret-test",
            MomoIpnUrl = "https://127.0.0.1/api/gateway/webhook", TimeoutSeconds = 30, AllowedReturnOrigins = ["https://shop.example.test"]
        });
        using var gateway = new MomoSandboxPaymentGateway(client, invalid);

        await Assert.ThrowsAsync<InvalidOperationException>(() => gateway.ChargeAsync(new GatewayChargeRequest(Guid.NewGuid(), 1_000m, "VND", "https://shop.example.test/return", "intent:" + Guid.NewGuid().ToString("N"))));
        Assert.Equal(0, calls);
    }

    private static byte[] IpN(string key, long amount, long transId, int resultCode)
    {
        const string partnerCode = "PARTNER_TEST";
        const string message = "Successful.";
        const string orderInfo = "HoaSap order payment";
        const string orderType = "momo_wallet";
        const string payType = "qr";
        const string extraData = "";
        var orderId = "I" + key;
        var requestId = "R" + key;
        const long responseTime = 1730000000000L;
        var data = $"accessKey=access-test&amount={amount}&extraData={extraData}&message={message}&orderId={orderId}&orderInfo={orderInfo}&orderType={orderType}&partnerCode={partnerCode}&payType={payType}&requestId={requestId}&responseTime={responseTime}&resultCode={resultCode}&transId={transId}";
        var payload = new
        {
            partnerCode, orderId, requestId, amount, transId, responseTime, resultCode, message,
            extraData, orderInfo, orderType, payType, signature = Sign("secret-test", data)
        };
        return JsonSerializer.SerializeToUtf8Bytes(payload);
    }

    private static string Sign(string secret, string data) => Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(data))).ToLowerInvariant();
    private static IOptions<CommerceGatewayOptions> Config(string endpoint = "") => Options.Create(new CommerceGatewayOptions
    {
        Provider = "MomoSandbox", Endpoint = endpoint, MomoPartnerCode = "PARTNER_TEST", MomoAccessKey = "access-test", MomoSecretKey = "secret-test",
        MomoIpnUrl = "https://shop.example.com/api/gateway/webhook", TimeoutSeconds = 30, AllowedReturnOrigins = ["https://shop.example.test"]
    });
    private static MomoSandboxPaymentGateway NewGateway(Handler handler) => new(new HttpClient(handler), Config());

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => responder(request, ct);
    }

    private sealed class Store : IAsyncDisposable
    {
        private readonly SqliteConnection connection = new("Data Source=:memory:");
        public HoaSapDbContext Db { get; private set; } = null!;
        public static async Task<Store> CreateAsync()
        {
            var store = new Store(); await store.connection.OpenAsync();
            store.Db = new HoaSapDbContext(new DbContextOptionsBuilder<HoaSapDbContext>().UseSqlite(store.connection).Options);
            await store.Db.Database.EnsureCreatedAsync(); return store;
        }
        public async Task<(Order order, GatewayPaymentIntent intent)> SeedUnknownAsync(string key, bool canceled)
        {
            var owner = Guid.NewGuid();
            var customer = new Customer { Id = owner, Email = owner + "@example.test" };
            var order = new Order { CustomerId = owner, OrderCode = "M-" + Guid.NewGuid().ToString("N"), TotalAmount = 12500m, SubTotal = 12500m, OrderStatus = canceled ? "Cancelled" : "Confirmed", PaymentStatus = "Unpaid" };
            var intent = new GatewayPaymentIntent { OrderId = order.Id, OwnerId = owner, ClientKey = Guid.NewGuid().ToString("N"), ProviderKey = "intent:" + key, Amount = 12500m, Currency = "VND", Status = "Unknown", ReconciliationStatus = "ReconciliationRequired" };
            Db.AddRange(customer, order, intent); await Db.SaveChangesAsync(); return (order, intent);
        }
        public async ValueTask DisposeAsync() { await Db.DisposeAsync(); await connection.DisposeAsync(); }
    }
}
