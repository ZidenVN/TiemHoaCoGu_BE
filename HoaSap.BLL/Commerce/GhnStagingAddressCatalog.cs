using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using HoaSap.BLL;
using Microsoft.Extensions.Options;

namespace HoaSap.BLL.Commerce;

public sealed class GhnStagingOptions
{
    public string Token { get; set; } = "";
    public int ShopId { get; set; }
    public bool FallbackCatalog { get; set; } = false;
}

public sealed record ShippingAreaDto(int Id, string Name, int? ParentId = null);

public interface IGhnStagingAddressCatalog
{
    Task<IReadOnlyList<ShippingAreaDto>> GetProvincesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<ShippingAreaDto>> GetWardsAsync(int provinceId, CancellationToken ct = default);
}

/// <summary>
/// Read-only GHN staging address directory for Vietnam's current two-level model.
/// The host is intentionally fixed to staging; this client has no order or fee methods.
/// </summary>
public sealed class GhnStagingAddressCatalog(HttpClient http, IOptions<GhnStagingOptions> options) : IGhnStagingAddressCatalog
{
    private const string StagingBase = "https://dev-online-gateway.ghn.vn/shiip/public-api/";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http = http;
    private readonly GhnStagingOptions _options = options.Value;

    public async Task<IReadOnlyList<ShippingAreaDto>> GetProvincesAsync(CancellationToken ct = default)
    {
        if (_options.FallbackCatalog) return GetFallbackProvinces();

        var data = await FetchAsync("v3/master-data/province/all?offset=0&limit=200", ct);
        if (data is null) throw Unavailable();
        return data.Where(x => x.Status == 1 && x.Id > 0 && !string.IsNullOrWhiteSpace(x.Name))
            .Select(x => new ShippingAreaDto(x.Id, x.Name.Trim(), x.ParentId))
            .ToArray();
    }

    public async Task<IReadOnlyList<ShippingAreaDto>> GetWardsAsync(int provinceId, CancellationToken ct = default)
    {
        if (provinceId <= 0) throw new BusinessException(400, "Mã tỉnh GHN không hợp lệ.");
        if (_options.FallbackCatalog) return GetFallbackWards(provinceId);

        var data = await FetchAsync($"v3/master-data/ward/all-by-province-id?province_id={provinceId}&offset=0&limit=200", ct);
        if (data is null) throw new BusinessException(400, "Tỉnh GHN không hợp lệ hoặc chưa có danh mục phường.");
        return data.Where(x => x.Status == 1 && x.Id > 0 && x.ParentId == provinceId && !string.IsNullOrWhiteSpace(x.Name))
            .Select(x => new ShippingAreaDto(x.Id, x.Name.Trim(), x.ParentId))
            .ToArray();
    }

    private static IReadOnlyList<ShippingAreaDto> GetFallbackProvinces() =>
    [
        new(1000001, "Hồ Chí Minh", 1),
        new(1000002, "Hà Nội", 1),
        new(1000003, "Đà Nẵng", 1),
        new(1000004, "Cần Thơ", 1),
        new(1000005, "Bình Dương", 1)
    ];

    private static IReadOnlyList<ShippingAreaDto> GetFallbackWards(int provinceId) => provinceId switch
    {
        1000001 =>
        [
            new(1003580, "Phường Thủ Đức", 1000001),
            new(1003581, "Phường Bến Nghé", 1000001),
            new(1003582, "Phường Bến Thành", 1000001),
            new(1003583, "Phường Tân Định", 1000001)
        ],
        1000002 =>
        [
            new(1002001, "Phường Tràng Tiền", 1000002),
            new(1002002, "Phường Hàng Trống", 1000002)
        ],
        _ =>
        [
            new(provinceId * 1000 + 1, "Phường 1", provinceId),
            new(provinceId * 1000 + 2, "Phường 2", provinceId)
        ]
    };

    private async Task<List<GhnArea>?> FetchAsync(string path, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_options.Token) || _options.ShopId <= 0) throw Unavailable();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(StagingBase), path));
            request.Headers.TryAddWithoutValidation("Token", _options.Token);
            request.Headers.TryAddWithoutValidation("ShopId", _options.ShopId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode) throw Unavailable();
            var envelope = await response.Content.ReadFromJsonAsync<GhnEnvelope>(JsonOptions, ct);
            if (envelope is null || envelope.Code != 200) throw Unavailable();
            return envelope.Data;
        }
        catch (BusinessException)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            throw Unavailable();
        }
    }

    private static BusinessException Unavailable() => new(503, "Danh mục địa chỉ GHN staging chưa sẵn sàng.");

    private sealed class GhnEnvelope
    {
        [JsonPropertyName("code")] public int Code { get; set; }
        [JsonPropertyName("data")] public List<GhnArea>? Data { get; set; }
    }

    private sealed class GhnArea
    {
        [JsonPropertyName("_id")] public int Id { get; set; }
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("parent_id")] public int? ParentId { get; set; }
        [JsonPropertyName("status")] public int Status { get; set; }
    }
}
