using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class Panoramas : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "panorama_privacy_status",
                table: "panorama");

            migrationBuilder.AddColumn<DateTime>(
                name: "created_at",
                table: "panorama",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "now()");

            migrationBuilder.AddColumn<int>(
                name: "height",
                table: "panorama",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "processing_error",
                table: "panorama",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "width",
                table: "panorama",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "panorama_original_retention_days",
                table: "organization",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_panorama_privacy_status",
                table: "panorama",
                column: "privacy_status");

            migrationBuilder.AddCheckConstraint(
                name: "panorama_privacy_status",
                table: "panorama",
                sql: "privacy_status IN ('pending', 'review', 'rendering', 'approved', 'failed')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_panorama_privacy_status",
                table: "panorama");

            migrationBuilder.DropCheckConstraint(
                name: "panorama_privacy_status",
                table: "panorama");

            migrationBuilder.DropColumn(
                name: "created_at",
                table: "panorama");

            migrationBuilder.DropColumn(
                name: "height",
                table: "panorama");

            migrationBuilder.DropColumn(
                name: "processing_error",
                table: "panorama");

            migrationBuilder.DropColumn(
                name: "width",
                table: "panorama");

            migrationBuilder.DropColumn(
                name: "panorama_original_retention_days",
                table: "organization");

            migrationBuilder.AddCheckConstraint(
                name: "panorama_privacy_status",
                table: "panorama",
                sql: "privacy_status IN ('pending', 'review', 'approved')");
        }
    }
}
