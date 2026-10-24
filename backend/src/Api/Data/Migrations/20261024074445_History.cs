using System;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class History : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "finalized_at",
                table: "node",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "finalized_by",
                table: "node",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "change_entry",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    organization_id = table.Column<long>(type: "bigint", nullable: false),
                    site_id = table.Column<long>(type: "bigint", nullable: false),
                    node_id = table.Column<long>(type: "bigint", nullable: false),
                    entity = table.Column<string>(type: "text", nullable: false),
                    entity_id = table.Column<long>(type: "bigint", nullable: false),
                    action = table.Column<string>(type: "text", nullable: false),
                    valid_from = table.Column<DateOnly>(type: "date", nullable: false),
                    recorded_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    recorded_by = table.Column<long>(type: "bigint", nullable: true),
                    recorded_by_name = table.Column<string>(type: "text", nullable: true),
                    reason = table.Column<string>(type: "text", nullable: true),
                    before = table.Column<JsonDocument>(type: "jsonb", nullable: true),
                    after = table.Column<JsonDocument>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_change_entry", x => x.id);
                    table.CheckConstraint("change_entry_action", "action IN ('finalize', 'create', 'update', 'delete')");
                    table.CheckConstraint("change_entry_entity", "entity IN ('node', 'anchor')");
                    table.ForeignKey(
                        name: "fk_change_entry_organization_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organization",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_change_entry_site_site_id_organization_id",
                        columns: x => new { x.site_id, x.organization_id },
                        principalTable: "site",
                        principalColumns: new[] { "id", "organization_id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_change_entry_entity_entity_id",
                table: "change_entry",
                columns: new[] { "entity", "entity_id" });

            migrationBuilder.CreateIndex(
                name: "ix_change_entry_node_id_valid_from",
                table: "change_entry",
                columns: new[] { "node_id", "valid_from" });

            migrationBuilder.CreateIndex(
                name: "ix_change_entry_organization_id",
                table: "change_entry",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "ix_change_entry_site_id_organization_id",
                table: "change_entry",
                columns: new[] { "site_id", "organization_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "change_entry");

            migrationBuilder.DropColumn(
                name: "finalized_at",
                table: "node");

            migrationBuilder.DropColumn(
                name: "finalized_by",
                table: "node");
        }
    }
}
