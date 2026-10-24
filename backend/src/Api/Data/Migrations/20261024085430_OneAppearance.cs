using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class OneAppearance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Existing duplicates are merged: the newest info point per (node, panorama) and the newest point per node
            // stay. Deleted anchors of finalized nodes are recorded in the history, as any other change of them.
            migrationBuilder.Sql("""
                CREATE TEMP TABLE merged_anchor ON COMMIT DROP AS
                SELECT a.* FROM anchor a
                JOIN (SELECT id, row_number() OVER (PARTITION BY node_id, kind, coalesce(panorama_id, 0) ORDER BY id DESC) AS rn
                      FROM anchor WHERE kind IN ('panorama_point', 'point')) r ON r.id = a.id
                WHERE r.rn > 1;

                INSERT INTO change_entry (organization_id, site_id, node_id, entity, entity_id, action, valid_from, recorded_at,
                                          recorded_by_name, reason, before)
                SELECT m.organization_id, n.site_id, m.node_id, 'anchor', m.id, 'delete',
                       greatest(current_date, coalesce((SELECT max(c.valid_from) FROM change_entry c WHERE c.entity = 'anchor' AND c.entity_id = m.id), current_date)),
                       now(), 'rendszer', 'Automatikus összevonás: elemenként egy pont, panorámánként egy infopont',
                       jsonb_build_object('kind', m.kind, 'panoramaId', m.panorama_id, 'yawDeg', m.yaw_deg, 'pitchDeg', m.pitch_deg,
                                          'position', jsonb_build_array(ST_X(m.position), ST_Y(m.position)), 'levelId', m.level_id, 'zRelM', m.z_rel_m)
                FROM merged_anchor m JOIN node n ON n.id = m.node_id
                WHERE n.finalized_at IS NOT NULL;

                DELETE FROM anchor WHERE id IN (SELECT id FROM merged_anchor);
                """);

            migrationBuilder.CreateIndex(
                name: "anchor_one_info_point_per_panorama",
                table: "anchor",
                columns: new[] { "node_id", "panorama_id" },
                unique: true,
                filter: "kind = 'panorama_point'");

            migrationBuilder.CreateIndex(
                name: "anchor_one_point",
                table: "anchor",
                column: "node_id",
                unique: true,
                filter: "kind = 'point'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "anchor_one_info_point_per_panorama",
                table: "anchor");

            migrationBuilder.DropIndex(
                name: "anchor_one_point",
                table: "anchor");
        }
    }
}
