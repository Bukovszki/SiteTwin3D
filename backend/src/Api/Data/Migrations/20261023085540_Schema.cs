using System;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;
using NetTopologySuite.Geometries;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class Schema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "organization",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    code = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    mapmanager_url = table.Column<string>(type: "text", nullable: true),
                    mapmanager_token_ref = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_organization", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "app_user",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    organization_id = table.Column<long>(type: "bigint", nullable: true),
                    username = table.Column<string>(type: "text", nullable: false),
                    username_lower = table.Column<string>(type: "text", nullable: false),
                    display_name = table.Column<string>(type: "text", nullable: false),
                    password_hash = table.Column<string>(type: "text", nullable: false),
                    role = table.Column<string>(type: "text", nullable: false),
                    active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    must_change_password = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    failed_logins = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    locked_until = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    security_stamp = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    last_login = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_app_user", x => x.id);
                    table.ForeignKey(
                        name: "fk_app_user_organization_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organization",
                        principalColumn: "id");
                });

            migrationBuilder.CreateTable(
                name: "node_type",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    organization_id = table.Column<long>(type: "bigint", nullable: true),
                    code = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    aspect = table.Column<string>(type: "text", nullable: false),
                    is_level = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    attr_schema = table.Column<JsonDocument>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_node_type", x => x.id);
                    table.CheckConstraint("node_type_aspect", "aspect IN ('location', 'function')");
                    table.ForeignKey(
                        name: "fk_node_type_organization_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organization",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "site",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    organization_id = table.Column<long>(type: "bigint", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    ref_point = table.Column<Point>(type: "geometry(Point, 4326)", nullable: false),
                    base_height_m = table.Column<double>(type: "double precision", nullable: false, defaultValue: 0.0),
                    extent = table.Column<Polygon>(type: "geometry(Polygon, 4326)", nullable: true),
                    attrs = table.Column<JsonDocument>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_site", x => x.id);
                    table.UniqueConstraint("ak_sites_id_organization_id", x => new { x.id, x.organization_id });
                    table.ForeignKey(
                        name: "fk_site_organization_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organization",
                        principalColumn: "id");
                });

            migrationBuilder.CreateTable(
                name: "map_layer",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    organization_id = table.Column<long>(type: "bigint", nullable: false),
                    site_id = table.Column<long>(type: "bigint", nullable: true),
                    source = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    layer = table.Column<string>(type: "text", nullable: false),
                    wms_params = table.Column<JsonDocument>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    external_url = table.Column<string>(type: "text", nullable: true),
                    sort_order = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    visible_default = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_map_layer", x => x.id);
                    table.CheckConstraint("map_layer_source", "source = 'mapmanager' OR (source = 'external_wms' AND external_url IS NOT NULL)");
                    table.ForeignKey(
                        name: "fk_map_layer_organization_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organization",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_map_layer_site_site_id_organization_id",
                        columns: x => new { x.site_id, x.organization_id },
                        principalTable: "site",
                        principalColumns: new[] { "id", "organization_id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "node",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    organization_id = table.Column<long>(type: "bigint", nullable: false),
                    site_id = table.Column<long>(type: "bigint", nullable: false),
                    parent_id = table.Column<long>(type: "bigint", nullable: true),
                    path = table.Column<string>(type: "ltree", nullable: false),
                    type_id = table.Column<long>(type: "bigint", nullable: false),
                    code = table.Column<string>(type: "text", nullable: true),
                    name = table.Column<string>(type: "text", nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    geom = table.Column<Geometry>(type: "geometry(Geometry, 4326)", nullable: true),
                    attrs = table.Column<JsonDocument>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_node", x => x.id);
                    table.UniqueConstraint("ak_node_id_organization_id", x => new { x.id, x.organization_id });
                    table.UniqueConstraint("ak_node_id_site_id", x => new { x.id, x.site_id });
                    table.ForeignKey(
                        name: "fk_node_node_type_type_id",
                        column: x => x.type_id,
                        principalTable: "node_type",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "fk_node_parent_same_site",
                        columns: x => new { x.parent_id, x.site_id },
                        principalTable: "node",
                        principalColumns: new[] { "id", "site_id" });
                    table.ForeignKey(
                        name: "fk_node_site_site_id_organization_id",
                        columns: x => new { x.site_id, x.organization_id },
                        principalTable: "site",
                        principalColumns: new[] { "id", "organization_id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "user_site_role",
                columns: table => new
                {
                    user_id = table.Column<long>(type: "bigint", nullable: false),
                    site_id = table.Column<long>(type: "bigint", nullable: false),
                    organization_id = table.Column<long>(type: "bigint", nullable: false),
                    role = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_site_role", x => new { x.user_id, x.site_id });
                    table.CheckConstraint("user_site_role_role", "role IN ('Editor', 'Viewer')");
                    table.ForeignKey(
                        name: "fk_user_site_role_app_user_user_id",
                        column: x => x.user_id,
                        principalTable: "app_user",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_user_site_role_sites_site_id_organization_id",
                        columns: x => new { x.site_id, x.organization_id },
                        principalTable: "site",
                        principalColumns: new[] { "id", "organization_id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "draw_layer",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    organization_id = table.Column<long>(type: "bigint", nullable: false),
                    site_id = table.Column<long>(type: "bigint", nullable: false),
                    level_id = table.Column<long>(type: "bigint", nullable: true),
                    name = table.Column<string>(type: "text", nullable: false),
                    geometry_type = table.Column<string>(type: "text", nullable: false),
                    structure = table.Column<JsonDocument>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    style = table.Column<JsonDocument>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    sort_order = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    created_by = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_draw_layer", x => x.id);
                    table.UniqueConstraint("ak_draw_layer_id_organization_id", x => new { x.id, x.organization_id });
                    table.CheckConstraint("draw_layer_geometry_type", "geometry_type IN ('Point', 'MultiLineString', 'MultiPolygon')");
                    table.ForeignKey(
                        name: "fk_draw_layer_node_level_id_organization_id",
                        columns: x => new { x.level_id, x.organization_id },
                        principalTable: "node",
                        principalColumns: new[] { "id", "organization_id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_draw_layer_site_site_id_organization_id",
                        columns: x => new { x.site_id, x.organization_id },
                        principalTable: "site",
                        principalColumns: new[] { "id", "organization_id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "floorplan",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    organization_id = table.Column<long>(type: "bigint", nullable: false),
                    level_id = table.Column<long>(type: "bigint", nullable: false),
                    source = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    layer = table.Column<string>(type: "text", nullable: false),
                    cql_filter = table.Column<string>(type: "text", nullable: true),
                    wms_params = table.Column<JsonDocument>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    external_url = table.Column<string>(type: "text", nullable: true),
                    sort_order = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    opacity = table.Column<float>(type: "real", nullable: false, defaultValue: 1f),
                    attrs = table.Column<JsonDocument>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_floorplan", x => x.id);
                    table.CheckConstraint("floorplan_source", "source = 'mapmanager' OR (source = 'external_wms' AND external_url IS NOT NULL)");
                    table.ForeignKey(
                        name: "fk_floorplan_node_level_id_organization_id",
                        columns: x => new { x.level_id, x.organization_id },
                        principalTable: "node",
                        principalColumns: new[] { "id", "organization_id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "model_asset",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    organization_id = table.Column<long>(type: "bigint", nullable: false),
                    site_id = table.Column<long>(type: "bigint", nullable: false),
                    node_id = table.Column<long>(type: "bigint", nullable: true),
                    kind = table.Column<string>(type: "text", nullable: false),
                    segmented = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    storage_key = table.Column<string>(type: "text", nullable: false),
                    attrs = table.Column<JsonDocument>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_model_asset", x => x.id);
                    table.UniqueConstraint("ak_model_asset_id_organization_id", x => new { x.id, x.organization_id });
                    table.CheckConstraint("model_asset_kind", "kind IN ('gltf', '3dtiles')");
                    table.ForeignKey(
                        name: "fk_model_asset_node_node_id_organization_id",
                        columns: x => new { x.node_id, x.organization_id },
                        principalTable: "node",
                        principalColumns: new[] { "id", "organization_id" });
                    table.ForeignKey(
                        name: "fk_model_asset_site_site_id_organization_id",
                        columns: x => new { x.site_id, x.organization_id },
                        principalTable: "site",
                        principalColumns: new[] { "id", "organization_id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "node_relation",
                columns: table => new
                {
                    from_id = table.Column<long>(type: "bigint", nullable: false),
                    to_id = table.Column<long>(type: "bigint", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    organization_id = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_node_relation", x => new { x.from_id, x.to_id, x.kind });
                    table.CheckConstraint("node_relation_not_self", "from_id <> to_id");
                    table.ForeignKey(
                        name: "fk_node_relation_node_from_id_organization_id",
                        columns: x => new { x.from_id, x.organization_id },
                        principalTable: "node",
                        principalColumns: new[] { "id", "organization_id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_node_relation_node_to_id_organization_id",
                        columns: x => new { x.to_id, x.organization_id },
                        principalTable: "node",
                        principalColumns: new[] { "id", "organization_id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "panorama",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    organization_id = table.Column<long>(type: "bigint", nullable: false),
                    node_id = table.Column<long>(type: "bigint", nullable: false),
                    storage_key = table.Column<string>(type: "text", nullable: false),
                    sha256 = table.Column<string>(type: "text", nullable: false),
                    position = table.Column<Point>(type: "geometry(Point, 4326)", nullable: false),
                    level_id = table.Column<long>(type: "bigint", nullable: true),
                    z_rel_m = table.Column<float>(type: "real", nullable: false, defaultValue: 1.6f),
                    heading_deg = table.Column<float>(type: "real", nullable: true),
                    captured_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    privacy_status = table.Column<string>(type: "text", nullable: false, defaultValue: "pending"),
                    privacy_regions = table.Column<JsonDocument>(type: "jsonb", nullable: false, defaultValueSql: "'[]'::jsonb"),
                    display_key = table.Column<string>(type: "text", nullable: true),
                    original_retained_until = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_panorama", x => x.id);
                    table.UniqueConstraint("ak_panorama_id_organization_id", x => new { x.id, x.organization_id });
                    table.CheckConstraint("panorama_privacy_status", "privacy_status IN ('pending', 'review', 'approved')");
                    table.ForeignKey(
                        name: "fk_panorama_node_level_id_organization_id",
                        columns: x => new { x.level_id, x.organization_id },
                        principalTable: "node",
                        principalColumns: new[] { "id", "organization_id" });
                    table.ForeignKey(
                        name: "fk_panorama_node_node_id_organization_id",
                        columns: x => new { x.node_id, x.organization_id },
                        principalTable: "node",
                        principalColumns: new[] { "id", "organization_id" });
                });

            migrationBuilder.CreateTable(
                name: "draw_record",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    organization_id = table.Column<long>(type: "bigint", nullable: false),
                    layer_id = table.Column<long>(type: "bigint", nullable: false),
                    seq = table.Column<int>(type: "integer", nullable: false),
                    attrs = table.Column<JsonDocument>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    node_id = table.Column<long>(type: "bigint", nullable: true),
                    version = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    modified_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    modified_by = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_draw_record", x => x.id);
                    table.UniqueConstraint("ak_draw_record_id_organization_id", x => new { x.id, x.organization_id });
                    table.ForeignKey(
                        name: "fk_draw_record_draw_layer_layer_id_organization_id",
                        columns: x => new { x.layer_id, x.organization_id },
                        principalTable: "draw_layer",
                        principalColumns: new[] { "id", "organization_id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_draw_record_node_node_id",
                        column: x => x.node_id,
                        principalTable: "node",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "anchor",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    organization_id = table.Column<long>(type: "bigint", nullable: false),
                    node_id = table.Column<long>(type: "bigint", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    model_asset_id = table.Column<long>(type: "bigint", nullable: true),
                    feature_ref = table.Column<string>(type: "text", nullable: true),
                    panorama_id = table.Column<long>(type: "bigint", nullable: true),
                    yaw_deg = table.Column<float>(type: "real", nullable: true),
                    pitch_deg = table.Column<float>(type: "real", nullable: true),
                    region_shape = table.Column<string>(type: "text", nullable: true),
                    size_x = table.Column<float>(type: "real", nullable: true),
                    size_y = table.Column<float>(type: "real", nullable: true),
                    size_z = table.Column<float>(type: "real", nullable: true),
                    heading_deg = table.Column<float>(type: "real", nullable: true),
                    footprint = table.Column<Polygon>(type: "geometry(Polygon, 4326)", nullable: true),
                    z_min = table.Column<float>(type: "real", nullable: true),
                    z_max = table.Column<float>(type: "real", nullable: true),
                    position = table.Column<Point>(type: "geometry(Point, 4326)", nullable: false),
                    level_id = table.Column<long>(type: "bigint", nullable: true),
                    z_rel_m = table.Column<float>(type: "real", nullable: false, defaultValue: 0f),
                    attrs = table.Column<JsonDocument>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_anchor", x => x.id);
                    table.CheckConstraint("anchor_kind_fields", "(kind = 'model_feature'  AND model_asset_id IS NOT NULL AND feature_ref IS NOT NULL) OR\n(kind = 'panorama_point' AND panorama_id IS NOT NULL AND yaw_deg IS NOT NULL AND pitch_deg IS NOT NULL) OR\n(kind = 'region' AND region_shape = 'box'\n                 AND size_x IS NOT NULL AND size_y IS NOT NULL AND size_z IS NOT NULL\n                 AND size_x > 0 AND size_y > 0 AND size_z > 0 AND heading_deg IS NOT NULL) OR\n(kind = 'region' AND region_shape = 'prism'\n                 AND footprint IS NOT NULL AND z_min IS NOT NULL AND z_max IS NOT NULL AND z_max > z_min) OR\n(kind = 'point')");
                    table.ForeignKey(
                        name: "fk_anchor_model_asset_model_asset_id_organization_id",
                        columns: x => new { x.model_asset_id, x.organization_id },
                        principalTable: "model_asset",
                        principalColumns: new[] { "id", "organization_id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_anchor_node_level_id_organization_id",
                        columns: x => new { x.level_id, x.organization_id },
                        principalTable: "node",
                        principalColumns: new[] { "id", "organization_id" });
                    table.ForeignKey(
                        name: "fk_anchor_node_node_id_organization_id",
                        columns: x => new { x.node_id, x.organization_id },
                        principalTable: "node",
                        principalColumns: new[] { "id", "organization_id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_anchor_panorama_panorama_id_organization_id",
                        columns: x => new { x.panorama_id, x.organization_id },
                        principalTable: "panorama",
                        principalColumns: new[] { "id", "organization_id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "panorama_link",
                columns: table => new
                {
                    from_id = table.Column<long>(type: "bigint", nullable: false),
                    to_id = table.Column<long>(type: "bigint", nullable: false),
                    organization_id = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_panorama_link", x => new { x.from_id, x.to_id });
                    table.ForeignKey(
                        name: "fk_panorama_link_panorama_from_id_organization_id",
                        columns: x => new { x.from_id, x.organization_id },
                        principalTable: "panorama",
                        principalColumns: new[] { "id", "organization_id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_panorama_link_panorama_to_id_organization_id",
                        columns: x => new { x.to_id, x.organization_id },
                        principalTable: "panorama",
                        principalColumns: new[] { "id", "organization_id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "draw_geometry",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    organization_id = table.Column<long>(type: "bigint", nullable: false),
                    record_id = table.Column<long>(type: "bigint", nullable: false),
                    layer_id = table.Column<long>(type: "bigint", nullable: false),
                    geom = table.Column<Geometry>(type: "geometry(Geometry, 4326)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_draw_geometry", x => x.id);
                    table.ForeignKey(
                        name: "fk_draw_geometry_draw_layer_layer_id_organization_id",
                        columns: x => new { x.layer_id, x.organization_id },
                        principalTable: "draw_layer",
                        principalColumns: new[] { "id", "organization_id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_draw_geometry_draw_record_record_id_organization_id",
                        columns: x => new { x.record_id, x.organization_id },
                        principalTable: "draw_record",
                        principalColumns: new[] { "id", "organization_id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "anchor_feature",
                table: "anchor",
                columns: new[] { "model_asset_id", "feature_ref" });

            migrationBuilder.CreateIndex(
                name: "ix_anchor_level_id_organization_id",
                table: "anchor",
                columns: new[] { "level_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "ix_anchor_model_asset_id_organization_id",
                table: "anchor",
                columns: new[] { "model_asset_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "ix_anchor_node_id_organization_id",
                table: "anchor",
                columns: new[] { "node_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "ix_anchor_panorama_id_organization_id",
                table: "anchor",
                columns: new[] { "panorama_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "ix_anchor_position",
                table: "anchor",
                column: "position")
                .Annotation("Npgsql:IndexMethod", "gist");

            migrationBuilder.CreateIndex(
                name: "ix_app_user_organization_id_username_lower",
                table: "app_user",
                columns: new[] { "organization_id", "username_lower" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "ix_draw_geometry_geom",
                table: "draw_geometry",
                column: "geom")
                .Annotation("Npgsql:IndexMethod", "gist");

            migrationBuilder.CreateIndex(
                name: "ix_draw_geometry_layer_id_organization_id",
                table: "draw_geometry",
                columns: new[] { "layer_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "ix_draw_geometry_record_id_organization_id",
                table: "draw_geometry",
                columns: new[] { "record_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "ix_draw_layer_level_id_organization_id",
                table: "draw_layer",
                columns: new[] { "level_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "ix_draw_layer_site_id_organization_id",
                table: "draw_layer",
                columns: new[] { "site_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "ix_draw_record_layer_id_organization_id",
                table: "draw_record",
                columns: new[] { "layer_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "ix_draw_record_node_id",
                table: "draw_record",
                column: "node_id");

            migrationBuilder.CreateIndex(
                name: "ix_floorplan_level_id_organization_id",
                table: "floorplan",
                columns: new[] { "level_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "ix_map_layer_organization_id",
                table: "map_layer",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "ix_map_layer_site_id_organization_id",
                table: "map_layer",
                columns: new[] { "site_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "ix_model_asset_node_id_organization_id",
                table: "model_asset",
                columns: new[] { "node_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "ix_model_asset_site_id_organization_id",
                table: "model_asset",
                columns: new[] { "site_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "ix_node_attrs",
                table: "node",
                column: "attrs")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "jsonb_path_ops" });

            migrationBuilder.CreateIndex(
                name: "ix_node_geom",
                table: "node",
                column: "geom")
                .Annotation("Npgsql:IndexMethod", "gist");

            migrationBuilder.CreateIndex(
                name: "ix_node_parent_id_site_id",
                table: "node",
                columns: new[] { "parent_id", "site_id" });

            migrationBuilder.CreateIndex(
                name: "ix_node_site_id_organization_id",
                table: "node",
                columns: new[] { "site_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "ix_node_type_id",
                table: "node",
                column: "type_id");

            migrationBuilder.CreateIndex(
                name: "node_path_gist",
                table: "node",
                column: "path")
                .Annotation("Npgsql:IndexMethod", "gist");

            migrationBuilder.CreateIndex(
                name: "ix_node_relation_from_id_organization_id",
                table: "node_relation",
                columns: new[] { "from_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "ix_node_relation_to_id_organization_id",
                table: "node_relation",
                columns: new[] { "to_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "ix_node_type_organization_id_code",
                table: "node_type",
                columns: new[] { "organization_id", "code" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "ix_organization_code",
                table: "organization",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_panorama_level_id_organization_id",
                table: "panorama",
                columns: new[] { "level_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "ix_panorama_node_id_organization_id",
                table: "panorama",
                columns: new[] { "node_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "ix_panorama_position",
                table: "panorama",
                column: "position")
                .Annotation("Npgsql:IndexMethod", "gist");

            migrationBuilder.CreateIndex(
                name: "ix_panorama_link_from_id_organization_id",
                table: "panorama_link",
                columns: new[] { "from_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "ix_panorama_link_to_id_organization_id",
                table: "panorama_link",
                columns: new[] { "to_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "ix_site_extent",
                table: "site",
                column: "extent")
                .Annotation("Npgsql:IndexMethod", "gist");

            migrationBuilder.CreateIndex(
                name: "ix_site_organization_id",
                table: "site",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "ix_user_site_role_site_id_organization_id",
                table: "user_site_role",
                columns: new[] { "site_id", "organization_id" });

            // Path triggers and shared base node types (db/sql). Edits of these scripts need a new migration.
            migrationBuilder.Sql(SqlScripts.Load("node_path.sql"));
            migrationBuilder.Sql(SqlScripts.Load("base_node_types.sql"));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(SqlScripts.Load("node_path_drop.sql"));

            migrationBuilder.DropTable(
                name: "anchor");

            migrationBuilder.DropTable(
                name: "draw_geometry");

            migrationBuilder.DropTable(
                name: "floorplan");

            migrationBuilder.DropTable(
                name: "map_layer");

            migrationBuilder.DropTable(
                name: "node_relation");

            migrationBuilder.DropTable(
                name: "panorama_link");

            migrationBuilder.DropTable(
                name: "user_site_role");

            migrationBuilder.DropTable(
                name: "model_asset");

            migrationBuilder.DropTable(
                name: "draw_record");

            migrationBuilder.DropTable(
                name: "panorama");

            migrationBuilder.DropTable(
                name: "app_user");

            migrationBuilder.DropTable(
                name: "draw_layer");

            migrationBuilder.DropTable(
                name: "node");

            migrationBuilder.DropTable(
                name: "node_type");

            migrationBuilder.DropTable(
                name: "site");

            migrationBuilder.DropTable(
                name: "organization");
        }
    }
}
