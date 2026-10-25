using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class NodeTypeExtension : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "node_type_extension",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    organization_id = table.Column<long>(type: "bigint", nullable: false),
                    node_type_id = table.Column<long>(type: "bigint", nullable: false),
                    attr_schema = table.Column<JsonDocument>(type: "json", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_node_type_extension", x => x.id);
                    table.ForeignKey(
                        name: "fk_node_type_extension_node_type_node_type_id",
                        column: x => x.node_type_id,
                        principalTable: "node_type",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_node_type_extension_organization_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organization",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_node_type_extension_node_type_id",
                table: "node_type_extension",
                column: "node_type_id");

            migrationBuilder.CreateIndex(
                name: "ix_node_type_extension_organization_id_node_type_id",
                table: "node_type_extension",
                columns: new[] { "organization_id", "node_type_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "node_type_extension");
        }
    }
}
