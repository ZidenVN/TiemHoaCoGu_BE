using System.ComponentModel.DataAnnotations;
using HoaSap.BLL;
using HoaSap.DAL;
using HoaSap.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.RegularExpressions;

namespace HoaSap.API.Controllers;

[ApiController, Authorize(Roles = "Admin"), Route("api/management")]
public sealed class CmsCatalogController(HoaSapDbContext db) : ControllerBase
{
    [HttpGet("categories")]
    public async Task<IActionResult> Categories(CancellationToken ct)
    {
        return Ok(await db.Categories.AsNoTracking().OrderBy(x => x.Name)
            .Select(x => new { x.Id, x.Code, x.Name, x.Description, x.ImageUrl }).ToListAsync(ct));
    }

    [HttpPost("categories")]
    public async Task<IActionResult> CreateCategory(AdminCategoryRequest request, CancellationToken ct)
    {
        var code = Required(request.Code, "Category code");
        var name = Required(request.Name, "Category name");
        ValidateUrl(request.ImageUrl, "Category image URL");
        if (await db.Categories.AnyAsync(x => x.Code == code, ct)) throw new BusinessException(409, "Category code exists");
        var category = new Category { Id = Guid.NewGuid(), Code = code, Name = name, Description = request.Description?.Trim() ?? "", ImageUrl = request.ImageUrl?.Trim() ?? "" };
        db.Categories.Add(category);
        await db.SaveChangesAsync(ct);
        return Ok(category);
    }

    [HttpPut("categories/{id:guid}")]
    public async Task<IActionResult> UpdateCategory(Guid id, AdminCategoryRequest request, CancellationToken ct)
    {
        var category = await db.Categories.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new BusinessException(404, "Category not found");
        var code = Required(request.Code, "Category code");
        var name = Required(request.Name, "Category name");
        ValidateUrl(request.ImageUrl, "Category image URL");
        if (await db.Categories.AnyAsync(x => x.Id != id && x.Code == code, ct)) throw new BusinessException(409, "Category code exists");
        category.Code = code; category.Name = name; category.Description = request.Description?.Trim() ?? ""; category.ImageUrl = request.ImageUrl?.Trim() ?? "";
        await db.SaveChangesAsync(ct);
        return Ok(category);
    }

    [HttpDelete("categories/{id:guid}")]
    public async Task<IActionResult> DeleteCategory(Guid id, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);
        var category = await db.Categories.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new BusinessException(404, "Category not found");
        if (await db.Products.AnyAsync(x => x.CategoryId == id, ct)) throw new BusinessException(409, "Category has products");
        db.Categories.Remove(category);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return NoContent();
    }

    [HttpGet("products")]
    public async Task<IActionResult> Products([FromQuery] CmsProductQuery query, CancellationToken ct)
    {
        var (page, pageSize) = Page(query.Page, query.PageSize);
        var items = db.Products.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(query.Search)) { var search = query.Search.Trim(); items = items.Where(x => x.Code.Contains(search) || x.Name.Contains(search)); }
        if (query.CategoryId is { } categoryId) items = items.Where(x => x.CategoryId == categoryId);
        if (query.IsActive is { } active) items = items.Where(x => x.IsActive == active);
        var total = await items.CountAsync(ct);
        var result = await items.OrderByDescending(x => x.CreatedAt).Skip((page - 1) * pageSize).Take(pageSize)
            .Select(x => new { x.Id, x.CategoryId, x.Code, x.Name, x.Price, x.OriginalPrice, x.ThumbnailUrl, x.Description, x.Badge, x.Section, x.IsCustomizable, x.IsPreOrder, x.ShippingWeightGrams, x.PackageLengthCm, x.PackageWidthCm, x.PackageHeightCm, x.PackagingSpecsConfirmed, x.IsActive, x.CreatedAt, quantityAvailable = x.Inventory == null ? 0 : x.Inventory.QuantityAvailable, quantityReserved = x.Inventory == null ? 0 : x.Inventory.QuantityReserved })
            .ToListAsync(ct);
        return Ok(new { items = result, total, page, pageSize });
    }

    [HttpGet("products/{id:guid}")]
    public async Task<IActionResult> Product(Guid id, CancellationToken ct)
    {
        var product = await db.Products.AsNoTracking().Include(x => x.Images).Include(x => x.Inventory).SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new BusinessException(404, "Product not found");
        return Ok(new { product.Id, product.CategoryId, product.Code, product.Name, product.Price, product.OriginalPrice, product.ThumbnailUrl, product.Description, product.Badge, product.Section, product.IsCustomizable, product.IsPreOrder, product.ShippingWeightGrams, product.PackageLengthCm, product.PackageWidthCm, product.PackageHeightCm, product.PackagingSpecsConfirmed, product.IsActive, product.CreatedAt, quantityAvailable = product.Inventory?.QuantityAvailable ?? 0, quantityReserved = product.Inventory?.QuantityReserved ?? 0, images = product.Images.OrderBy(x => x.DisplayOrder).Select(x => new { x.Id, x.ImageUrl, x.DisplayOrder }) });
    }

    [HttpPut("products/{id:guid}")]
    public async Task<IActionResult> UpdateProduct(Guid id, AdminProductRequest request, CancellationToken ct)
    {
        var product = await db.Products.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new BusinessException(404, "Product not found");
        await ValidateProduct(request, id, ct);
        product.CategoryId = request.CategoryId; product.Code = Required(request.Code, "Product code"); product.Name = Required(request.Name, "Product name"); product.Price = request.Price; product.OriginalPrice = request.OriginalPrice; product.ThumbnailUrl = request.ThumbnailUrl?.Trim() ?? ""; product.Description = request.Description?.Trim() ?? ""; product.Badge = request.Badge?.Trim() ?? ""; product.Section = request.Section?.Trim() ?? ""; product.IsCustomizable = request.IsCustomizable; product.IsPreOrder = request.IsPreOrder;
        if (request.PackagingFieldsWereProvided)
        {
            product.ShippingWeightGrams = request.ShippingWeightGrams;
            product.PackageLengthCm = request.PackageLengthCm;
            product.PackageWidthCm = request.PackageWidthCm;
            product.PackageHeightCm = request.PackageHeightCm;
            product.PackagingSpecsConfirmed = request.PackagingSpecsConfirmed;
        }
        product.IsActive = request.IsActive;
        await db.SaveChangesAsync(ct); return Ok(product);
    }

    [HttpDelete("products/{id:guid}")]
    public async Task<IActionResult> DeleteProduct(Guid id, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);
        var product = await db.Products.Include(x => x.Inventory).SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new BusinessException(404, "Product not found");
        if (await db.OrderItems.AnyAsync(x => x.ProductId == id, ct) || await db.ShoppingCartItems.AnyAsync(x => x.ProductId == id, ct) || product.Inventory?.QuantityReserved > 0) throw new BusinessException(409, "Product is referenced or has reserved stock");
        if (product.Inventory is not null) db.Inventories.Remove(product.Inventory);
        db.Products.Remove(product);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return NoContent();
    }

    [HttpGet("products/{id:guid}/images")]
    public async Task<IActionResult> Images(Guid id, CancellationToken ct)
    {
        if (!await db.Products.AnyAsync(x => x.Id == id, ct)) throw new BusinessException(404, "Product not found");
        return Ok(await db.ProductImages.AsNoTracking().Where(x => x.ProductId == id).OrderBy(x => x.DisplayOrder).Select(x => new { x.Id, x.ImageUrl, x.DisplayOrder }).ToListAsync(ct));
    }

    [HttpPost("products/{id:guid}/images")]
    public Task<IActionResult> AddImage(Guid id, ProductImageRequest request, CancellationToken ct) => SaveImage(id, null, request, ct);

    [HttpPut("products/{id:guid}/images/{imageId:guid}")]
    public Task<IActionResult> EditImage(Guid id, Guid imageId, ProductImageRequest request, CancellationToken ct) => SaveImage(id, imageId, request, ct);

    [HttpDelete("products/{id:guid}/images/{imageId:guid}")]
    public async Task<IActionResult> DeleteImage(Guid id, Guid imageId, CancellationToken ct)
    {
        var image = await db.ProductImages.SingleOrDefaultAsync(x => x.Id == imageId && x.ProductId == id, ct) ?? throw new BusinessException(404, "Product image not found");
        db.ProductImages.Remove(image); await db.SaveChangesAsync(ct); return NoContent();
    }

    private async Task<IActionResult> SaveImage(Guid productId, Guid? imageId, ProductImageRequest request, CancellationToken ct)
    {
        if (!await db.Products.AnyAsync(x => x.Id == productId, ct)) throw new BusinessException(404, "Product not found");
        ValidateUrl(request.ImageUrl, "Image URL");
        if (request.DisplayOrder < 0) throw new BusinessException(400, "Display order cannot be negative");
        var image = imageId is null ? new ProductImage { Id = Guid.NewGuid(), ProductId = productId } : await db.ProductImages.SingleOrDefaultAsync(x => x.Id == imageId && x.ProductId == productId, ct) ?? throw new BusinessException(404, "Product image not found");
        if (await db.ProductImages.AnyAsync(x => x.ProductId == productId && x.DisplayOrder == request.DisplayOrder && x.Id != image.Id, ct)) throw new BusinessException(409, "Display order exists");
        image.ImageUrl = request.ImageUrl.Trim(); image.DisplayOrder = request.DisplayOrder;
        if (imageId is null) db.ProductImages.Add(image);
        await db.SaveChangesAsync(ct); return Ok(new { image.Id, image.ImageUrl, image.DisplayOrder });
    }

    private async Task ValidateProduct(AdminProductRequest request, Guid? id, CancellationToken ct)
    {
        request.ValidatePackaging();
        if (request.Price < 0 || request.OriginalPrice is < 0 || decimal.Round(request.Price, 2) != request.Price || (request.OriginalPrice is { } original && decimal.Round(original, 2) != original)) throw new BusinessException(400, "Price is invalid");
        if (!await db.Categories.AnyAsync(x => x.Id == request.CategoryId, ct)) throw new BusinessException(404, "Category not found");
        var code = Required(request.Code, "Product code"); Required(request.Name, "Product name"); ValidateUrl(request.ThumbnailUrl, "Thumbnail URL");
        if (await db.Products.AnyAsync(x => x.Id != id && x.Code == code, ct)) throw new BusinessException(409, "Product code exists");
    }

    private static string Required(string? value, string field) { var result = value?.Trim() ?? ""; if (result.Length == 0) throw new BusinessException(400, $"{field} is required"); return result; }
    private static void ValidateUrl(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        var candidate = value.Trim();
        if (Regex.IsMatch(candidate, @"^/api/media/[0-9a-fA-F]{32}\.(png|jpg|webp)$", RegexOptions.CultureInvariant)) return;
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0 || candidate.Any(char.IsWhiteSpace))
            throw new BusinessException(400, $"{field} must be an HTTP(S) URL or managed media URL");
    }
    private static (int Page, int Size) Page(int page, int size)
    {
        if (page < 1 || size < 1 || size > 100 || ((long)page - 1) * size > int.MaxValue)
            throw new BusinessException(400, "Invalid paging");
        return (page, size);
    }
}

public sealed class CmsProductQuery { public string? Search { get; set; } public Guid? CategoryId { get; set; } public bool? IsActive { get; set; } = null; public int Page { get; set; } = 1; public int PageSize { get; set; } = 20; }
public sealed class ProductImageRequest { [Required, StringLength(2000)] public string ImageUrl { get; set; } = ""; public int DisplayOrder { get; set; } }
