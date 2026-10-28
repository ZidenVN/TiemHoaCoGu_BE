using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HoaSap.DAL.Migrations
{
    /// <inheritdoc />
    public partial class AddConfirmedPackagingSpecs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Product_ShippingWeight",
                table: "Products");

            migrationBuilder.AlterColumn<int>(
                name: "ShippingWeightGrams",
                table: "Products",
                type: "int",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "int",
                oldDefaultValue: 500);

            migrationBuilder.AddColumn<decimal>(
                name: "PackageHeightCm",
                table: "Products",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "PackageLengthCm",
                table: "Products",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "PackageWidthCm",
                table: "Products",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "PackagingSpecsConfirmed",
                table: "Products",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddCheckConstraint(
                name: "CK_Product_ConfirmedPackaging",
                table: "Products",
                sql: "PackagingSpecsConfirmed = 0 OR (ShippingWeightGrams IS NOT NULL AND ShippingWeightGrams > 0 AND PackageLengthCm IS NOT NULL AND PackageLengthCm > 0 AND PackageWidthCm IS NOT NULL AND PackageWidthCm > 0 AND PackageHeightCm IS NOT NULL AND PackageHeightCm > 0)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Product_PackageDimensions",
                table: "Products",
                sql: "(PackageLengthCm IS NULL OR PackageLengthCm > 0) AND (PackageWidthCm IS NULL OR PackageWidthCm > 0) AND (PackageHeightCm IS NULL OR PackageHeightCm > 0)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Product_ShippingWeight",
                table: "Products",
                sql: "ShippingWeightGrams IS NULL OR ShippingWeightGrams > 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Product_ConfirmedPackaging",
                table: "Products");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Product_PackageDimensions",
                table: "Products");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Product_ShippingWeight",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "PackageHeightCm",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "PackageLengthCm",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "PackageWidthCm",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "PackagingSpecsConfirmed",
                table: "Products");

            migrationBuilder.AlterColumn<int>(
                name: "ShippingWeightGrams",
                table: "Products",
                type: "int",
                nullable: false,
                defaultValue: 500,
                oldClrType: typeof(int),
                oldType: "int",
                oldNullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_Product_ShippingWeight",
                table: "Products",
                sql: "ShippingWeightGrams > 0");
        }
    }
}
