using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HoaSap.BLL.Commerce;
using Microsoft.Extensions.Options;
using Xunit;

namespace HoaSap.Tests;

public sealed class MomoSandboxQueryTests
{
    [Theory]
    [InlineData(0, 1_000L, 99887766L, "paid")]
    [InlineData(1006, 1_000L, 0L, "not-charged")]
    [InlineData(7002, 1_000L, 0L, "pending")]
    [InlineData(1006, 0L, 0L, "unknown")]
    [InlineData(9000, 1_000L, 99887766L, "authorized")]
    public async Task Query_uses_signed_unique_request_and_conservative_outcome(int resultCode, long amount, long transId, string expectedOutcome)
    {
        var intentKey = Guid.NewGuid().ToString("N");
        string? url = null;
        string? queryRequestId = null;
        var handler = new Handler(async (request, ct) =>
        {
            url = request.RequestUri?.ToString();
            var raw = await request.Content!.ReadAsStringAsync(ct);
            using var body = JsonDocument.Parse(raw);
            var root = body.RootElement;
            var orderId = root.GetProperty("orderId").GetString()!;
            queryRequestId = root.GetProperty("requestId").GetString()!;
            Assert.Equal("I" + intentKey, orderId);
            Assert.StartsWith("Q", queryRequestId);
            Assert.Equal(33, queryRequestId.Length);
            var signatureData = $"accessKey=access-test&orderId={orderId}&partnerCode=PARTNER_TEST&requestId={queryRequestId}";
            Assert.Equal(Sign("secret-test", signatureData), root.GetProperty("signature").GetString());
            Assert.Equal("vi", root.GetProperty("lang").GetString());
            var response = new { partnerCode = "PARTNER_TEST", requestId = queryRequestId, orderId, amount, transId, resultCode, message = "Sandbox query result", responseTime = 1730000000000L };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(response), Encoding.UTF8, "application/json") };
        });
        using var gateway = new MomoSandboxPaymentGateway(new HttpClient(handler), Config());

        var result = await gateway.QueryAsync("intent:" + intentKey, 1_000m);

        Assert.Equal("https://test-payment.momo.vn/v2/gateway/api/query", url);
        Assert.Equal(expectedOutcome, result.Outcome);
        Assert.Equal(resultCode, result.ResultCode);
        Assert.Equal("I" + intentKey, result.ProviderOrderId);
        Assert.Equal(queryRequestId, result.RequestId);
        Assert.Equal(amount, (long)result.Amount);
        Assert.Equal(transId > 0 ? transId.ToString() : "", result.TransactionCode);
    }

    [Fact]
    public async Task Query_rejects_a_response_correlated_to_a_different_order()
    {
        var intentKey = Guid.NewGuid().ToString("N");
        var handler = new Handler(async (request, ct) =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var requestId = body.RootElement.GetProperty("requestId").GetString()!;
            var response = new { partnerCode = "PARTNER_TEST", requestId, orderId = "I" + Guid.NewGuid().ToString("N"), amount = 1_000L, transId = 99887766L, resultCode = 0, message = "Successful", responseTime = 1730000000000L };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(response), Encoding.UTF8, "application/json") };
        });
        using var gateway = new MomoSandboxPaymentGateway(new HttpClient(handler), Config());

        await Assert.ThrowsAsync<InvalidOperationException>(() => gateway.QueryAsync("intent:" + intentKey, 1_000m));
    }

    private static string Sign(string secret, string data) => Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(data))).ToLowerInvariant();
    private static IOptions<CommerceGatewayOptions> Config() => Options.Create(new CommerceGatewayOptions
    {
        Provider = "MomoSandbox", MomoPartnerCode = "PARTNER_TEST", MomoAccessKey = "access-test", MomoSecretKey = "secret-test",
        MomoIpnUrl = "https://shop.example.com/api/gateway/webhook", TimeoutSeconds = 30, AllowedReturnOrigins = ["https://shop.example.test"]
    });
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => responder(request, ct);
    }
}
