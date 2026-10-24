using HoaSap.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace HoaSap.DAL.Configurations;

public sealed class ProductShippingConfiguration : IEntityTypeConfiguration<Product>
{
    public void Configure(EntityTypeBuilder<Product> b)
    {
        b.Property(x => x.ShippingWeightGrams);
        b.Property(x => x.PackageLengthCm).HasPrecision(18, 2);
        b.Property(x => x.PackageWidthCm).HasPrecision(18, 2);
        b.Property(x => x.PackageHeightCm).HasPrecision(18, 2);
        b.Property(x => x.PackagingSpecsConfirmed).HasDefaultValue(false);
        b.ToTable("Products", t =>
        {
            t.HasCheckConstraint("CK_Product_ShippingWeight", "ShippingWeightGrams IS NULL OR ShippingWeightGrams > 0");
            t.HasCheckConstraint("CK_Product_PackageDimensions", "(PackageLengthCm IS NULL OR PackageLengthCm > 0) AND (PackageWidthCm IS NULL OR PackageWidthCm > 0) AND (PackageHeightCm IS NULL OR PackageHeightCm > 0)");
            t.HasCheckConstraint("CK_Product_ConfirmedPackaging", "PackagingSpecsConfirmed = 0 OR (ShippingWeightGrams IS NOT NULL AND ShippingWeightGrams > 0 AND PackageLengthCm IS NOT NULL AND PackageLengthCm > 0 AND PackageWidthCm IS NOT NULL AND PackageWidthCm > 0 AND PackageHeightCm IS NOT NULL AND PackageHeightCm > 0)");
        });
    }
}
