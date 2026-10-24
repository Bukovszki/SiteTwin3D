using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class ModelConversion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "processing_error",
                table: "model_asset",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "source_format",
                table: "model_asset",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "status",
                table: "model_asset",
                type: "text",
                nullable: false,
                defaultValue: "ready");

            migrationBuilder.CreateIndex(
                name: "model_asset_converting",
                table: "model_asset",
                column: "status",
                filter: "status = 'converting'");

            migrationBuilder.AddCheckConstraint(
                name: "model_asset_status",
                table: "model_asset",
                sql: "status IN ('ready', 'converting', 'failed')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "model_asset_converting",
                table: "model_asset");

            migrationBuilder.DropCheckConstraint(
                name: "model_asset_status",
                table: "model_asset");

            migrationBuilder.DropColumn(
                name: "processing_error",
                table: "model_asset");

            migrationBuilder.DropColumn(
                name: "source_format",
                table: "model_asset");

            migrationBuilder.DropColumn(
                name: "status",
                table: "model_asset");
        }
    }
}
