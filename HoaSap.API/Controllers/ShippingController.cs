using System.Security.Claims;
using HoaSap.BLL;
using HoaSap.BLL.Commerce;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HoaSap.API.Controllers;

public sealed record ShippingQuoteInput(string StreetAddress, int ProvinceId, int WardId, List<CartItemRequest>? Items = null);

[ApiController, Route("api/shipping")]
public sealed class ShippingController(ShippingQuoteService service, IGhnStagingAddressCatalog addressCatalog) : ControllerBase
{
    [HttpGet("locations/provinces"), Authorize]
    public Task<IReadOnlyList<ShippingAreaDto>> Provinces(CancellationToken ct) => addressCatalog.GetProvincesAsync(ct);

    [HttpGet("locations/wards"), Authorize]
    public Task<IReadOnlyList<ShippingAreaDto>> Wards([FromQuery] int provinceId, CancellationToken ct) => addressCatalog.GetWardsAsync(provinceId, ct);

    [HttpPost("quote"), Authorize]
    public async Task<ShippingQuoteDto> Quote(ShippingQuoteInput request, CancellationToken ct)
    {
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id)) throw new UnauthorizedAccessException();
        if (request is null || request.ProvinceId <= 0 || request.WardId <= 0)
            throw new BusinessException(400, "Chọn tỉnh và phường hợp lệ từ danh mục GHN.");

        var province = (await addressCatalog.GetProvincesAsync(ct)).SingleOrDefault(x => x.Id == request.ProvinceId);
        if (province is null) throw new BusinessException(400, "Tỉnh không thuộc danh mục GHN staging hiện tại.");
        var ward = (await addressCatalog.GetWardsAsync(province.Id, ct)).SingleOrDefault(x => x.Id == request.WardId && x.ParentId == province.Id);
        if (ward is null) throw new BusinessException(400, "Phường không thuộc tỉnh đã chọn trong danh mục GHN staging.");

        var quoteRequest = new ShippingQuoteRequest(request.StreetAddress, province.Id, province.Name, ward.Id, ward.Name, Items: request.Items);
        return await service.QuoteAsync(id, quoteRequest, ct);
    }
}
