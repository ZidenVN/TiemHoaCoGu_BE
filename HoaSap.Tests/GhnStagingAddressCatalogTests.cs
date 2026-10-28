using System.Net;
using System.Net.Http.Headers;
using HoaSap.BLL;
using HoaSap.BLL.Commerce;
using Microsoft.Extensions.Options;
using Xunit;

namespace HoaSap.Tests;

public sealed class GhnStagingAddressCatalogTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpMethod Method, Uri? Uri, string? Token, string? ShopId)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.Method, request.RequestUri,
                request.Headers.TryGetValues("Token", out var token) ? token.Single() : null,
                request.Headers.TryGetValues("ShopId", out var shopId) ? shopId.Single() : null));
            return Task.FromResult(respond(request));
        }
    }

    [Fact]
    public async Task Provinces_use_fixed_staging_endpoint_and_only_return_active_canonical_entries()
    {
        const string body = """{"code":200,"data":[{"_id":1000001,"name":" Hồ Chí Minh ","parent_id":1,"status":1},{"_id":1000002,"name":"Inactive","parent_id":1,"status":2},{"_id":0,"name":"Bad","parent_id":1,"status":1}]}""";
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        using var http = new HttpClient(handler);
        var service = new GhnStagingAddressCatalog(http, Options.Create(new GhnStagingOptions { Token = "synthetic-token", ShopId = 232722 }));

        var areas = await service.GetProvincesAsync();

        Assert.Equal([new ShippingAreaDto(1000001, "Hồ Chí Minh", 1)], areas);
        var call = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, call.Method);
        Assert.Equal("https://dev-online-gateway.ghn.vn/shiip/public-api/v3/master-data/province/all?offset=0&limit=200", call.Uri!.ToString());
        Assert.Equal("synthetic-token", call.Token);
        Assert.Equal("232722", call.ShopId);
    }

    [Fact]
    public async Task Wards_use_selected_new_province_id_and_filter_inactive_or_foreign_rows()
    {
        const string body = """{"code":200,"data":[{"_id":1003580,"name":"Phường Thủ Đức","parent_id":1000001,"status":1},{"_id":1003581,"name":"Inactive","parent_id":1000001,"status":2},{"_id":1003582,"name":"Foreign","parent_id":1000002,"status":1}]}""";
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        using var http = new HttpClient(handler);
        var service = new GhnStagingAddressCatalog(http, Options.Create(new GhnStagingOptions { Token = "synthetic-token", ShopId = 232722 }));

        var areas = await service.GetWardsAsync(1000001);

        Assert.Equal([new ShippingAreaDto(1003580, "Phường Thủ Đức", 1000001)], areas);
        var call = Assert.Single(handler.Requests);
        Assert.Equal("https://dev-online-gateway.ghn.vn/shiip/public-api/v3/master-data/ward/all-by-province-id?province_id=1000001&offset=0&limit=200", call.Uri!.ToString());
    }

    [Fact]
    public async Task Missing_credentials_fail_closed_without_sending_a_request()
    {
        var handler = new StubHandler(_ => throw new Xunit.Sdk.XunitException("Unexpected provider call"));
        using var http = new HttpClient(handler);
        var service = new GhnStagingAddressCatalog(http, Options.Create(new GhnStagingOptions()));

        var error = await Assert.ThrowsAsync<BusinessException>(() => service.GetProvincesAsync());

        Assert.Equal(503, error.StatusCode);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Invalid_province_and_upstream_failure_are_rejected_without_raw_response()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("synthetic upstream error") });
        using var http = new HttpClient(handler);
        var service = new GhnStagingAddressCatalog(http, Options.Create(new GhnStagingOptions { Token = "synthetic-token", ShopId = 232722 }));

        var invalid = await Assert.ThrowsAsync<BusinessException>(() => service.GetWardsAsync(0));
        var upstream = await Assert.ThrowsAsync<BusinessException>(() => service.GetProvincesAsync());

        Assert.Equal(400, invalid.StatusCode);
        Assert.Equal(503, upstream.StatusCode);
        Assert.Single(handler.Requests);
    }
}
