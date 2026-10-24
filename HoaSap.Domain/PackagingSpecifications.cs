using HoaSap.Domain.Entities;

namespace HoaSap.Domain;

public static class PackagingSpecifications
{
    public static bool IsConfirmedAndComplete(Product product) =>
        product.PackagingSpecsConfirmed &&
        product.ShippingWeightGrams is > 0 and <= 1_000_000 &&
        product.PackageLengthCm is > 0 and <= 10_000m &&
        product.PackageWidthCm is > 0 and <= 10_000m &&
        product.PackageHeightCm is > 0 and <= 10_000m;
}
