using Api.Model;
using System.Linq.Expressions;
using Api.Services;
using Api.Services.Auth;
using Api.Services.History;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Api.Data;

/// <summary>
/// Application database (PostgreSQL + PostGIS + ltree), schema per docs/SPECIFICATION.md chapter 8. Every geometry
/// is stored in WGS84 (EPSG:4326).
///
/// Organization isolation (spec 3.1):
/// <list type="bullet">
/// <item>every <see cref="IOrganizationOwned"/> entity has a global query filter on the signed-in user's organization,
/// so a forgotten condition never leads to another organization's data (→ 404). Without an organization
/// (platform admin, background job) the filter lets nothing through; use IgnoreQueryFilters deliberately.</item>
/// <item>new rows get the organization on save automatically.</item>
/// <item>composite foreign keys (reference id + organization_id) guarantee in the database that a row never
/// points into another organization.</item>
/// </list>
/// A new organization-owned table MUST be registered with <see cref="OrganizationOwned{T}"/> (a test checks it).
/// </summary>
public partial class AppDbContext(DbContextOptions<AppDbContext> options, CurrentUser? currentUser = null, ChangeContext? changes = null)
    : DbContext(options)
{
    /// <summary>The filter value, evaluated per query.</summary>
    private long? CurrentOrganizationId => currentUser?.OrganizationId;

    public DbSet<Organization> Organizations => Set<Organization>();
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<UserSiteRole> UserSiteRoles => Set<UserSiteRole>();
    public DbSet<Site> Sites => Set<Site>();
    public DbSet<NodeType> NodeTypes => Set<NodeType>();
    public DbSet<Node> Nodes => Set<Node>();
    public DbSet<NodeRelation> NodeRelations => Set<NodeRelation>();
    public DbSet<Panorama> Panoramas => Set<Panorama>();
    public DbSet<PanoramaLink> PanoramaLinks => Set<PanoramaLink>();
    public DbSet<ModelAsset> ModelAssets => Set<ModelAsset>();
    public DbSet<Floorplan> Floorplans => Set<Floorplan>();
    public DbSet<MapLayer> MapLayers => Set<MapLayer>();
    public DbSet<Anchor> Anchors => Set<Anchor>();
    public DbSet<DrawLayer> DrawLayers => Set<DrawLayer>();
    public DbSet<DrawRecord> DrawRecords => Set<DrawRecord>();
    public DbSet<DrawGeometry> DrawGeometries => Set<DrawGeometry>();
    public DbSet<ChangeEntry> ChangeEntries => Set<ChangeEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasPostgresExtension("postgis");
        modelBuilder.HasPostgresExtension("ltree");

        modelBuilder.Entity<Organization>(e =>
        {
            e.ToTable("organization");
            e.HasIndex(x => x.Code).IsUnique();
            e.Property(x => x.Active).HasDefaultValue(true).ValueGeneratedNever();
            e.Property(x => x.CreatedAt).HasDefaultValueSql("now()").ValueGeneratedNever();
        });

        modelBuilder.Entity<AppUser>(e =>
        {
            e.ToTable("app_user");
            e.Property(x => x.Active).HasDefaultValue(true).ValueGeneratedNever();
            e.Property(x => x.MustChangePassword).HasDefaultValue(true).ValueGeneratedNever();
            e.Property(x => x.FailedLogins).HasDefaultValue(0).ValueGeneratedNever();
            e.Property(x => x.CreatedAt).HasDefaultValueSql("now()").ValueGeneratedNever();
            e.HasOne(x => x.Organization).WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.NoAction);
            // Unique inside the organization; platform admins (no organization) are unique among themselves.
            e.HasIndex(x => new { x.OrganizationId, x.UsernameLower }).IsUnique().AreNullsDistinct(false);
            e.HasQueryFilter(x => x.OrganizationId == CurrentOrganizationId);
        });

        modelBuilder.Entity<UserSiteRole>(e =>
        {
            e.ToTable("user_site_role");
            e.HasKey(x => new { x.UserId, x.SiteId });
            // app_user.organization_id is nullable (platform admin), so it cannot be part of a key: the service
            // checks that the user belongs to the site's organization.
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            References<UserSiteRole, Site>(e, DeleteBehavior.Cascade, nameof(UserSiteRole.SiteId));
            e.ToTable(t => t.HasCheckConstraint("user_site_role_role", "role IN ('Editor', 'Viewer')"));
        });

        modelBuilder.Entity<Site>(e =>
        {
            e.ToTable("site");
            e.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.NoAction);
            e.Property(x => x.RefPoint).HasColumnType("geometry(Point, 4326)");
            e.Property(x => x.Extent).HasColumnType("geometry(Polygon, 4326)");
            e.Property(x => x.BaseHeightM).HasDefaultValue(0d).ValueGeneratedNever();
            e.Property(x => x.Attrs).HasDefaultValueSql("'{}'::jsonb");
            e.HasIndex(x => x.Extent).HasMethod("gist");
        });

        modelBuilder.Entity<NodeType>(e =>
        {
            e.ToTable("node_type");
            // json, not jsonb: jsonb reorders keys, and the property order is the order of form fields and grid columns.
            e.Property(x => x.AttrSchema).HasColumnType("json");
            e.Property(x => x.IsLevel).HasDefaultValue(false).ValueGeneratedNever();
            e.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.OrganizationId, x.Code }).IsUnique().AreNullsDistinct(false);
            e.ToTable(t => t.HasCheckConstraint("node_type_aspect", "aspect IN ('location', 'function')"));
            // Shared base types (no organization) and the organization's own types.
            e.HasQueryFilter(x => x.OrganizationId == null || x.OrganizationId == CurrentOrganizationId);
        });

        modelBuilder.Entity<Node>(e =>
        {
            e.ToTable("node");
            References<Node, Site>(e, DeleteBehavior.Cascade, nameof(Node.SiteId));
            // Parent and child on the same site (spec 8.3 note): enforced by the composite key.
            e.HasOne<Node>().WithMany().HasForeignKey(x => new { x.ParentId, x.SiteId })
                .HasPrincipalKey(x => new { x.Id, x.SiteId }).OnDelete(DeleteBehavior.NoAction)
                .HasConstraintName("fk_node_parent_same_site");
            e.HasOne(x => x.Type).WithMany().HasForeignKey(x => x.TypeId).OnDelete(DeleteBehavior.NoAction);
            // Maintained by the node_path triggers (db/sql/node_path.sql); read back after insert and update.
            e.Property(x => x.Path).HasColumnType("ltree").ValueGeneratedOnAddOrUpdate();
            e.Property(x => x.SortOrder).HasDefaultValue(0).ValueGeneratedNever();
            e.Property(x => x.Geom).HasColumnType("geometry(Geometry, 4326)");
            e.Property(x => x.Attrs).HasDefaultValueSql("'{}'::jsonb");
            e.HasIndex(x => x.Path).HasMethod("gist").HasDatabaseName("node_path_gist");
            e.HasIndex(x => x.Geom).HasMethod("gist");
            e.HasIndex(x => x.Attrs).HasMethod("gin").HasOperators("jsonb_path_ops");
        });

        modelBuilder.Entity<NodeRelation>(e =>
        {
            e.ToTable("node_relation");
            e.HasKey(x => new { x.FromId, x.ToId, x.Kind });
            References<NodeRelation, Node>(e, DeleteBehavior.Cascade, nameof(NodeRelation.FromId));
            References<NodeRelation, Node>(e, DeleteBehavior.Cascade, nameof(NodeRelation.ToId));
            e.ToTable(t => t.HasCheckConstraint("node_relation_not_self", "from_id <> to_id"));
        });

        modelBuilder.Entity<Panorama>(e =>
        {
            e.ToTable("panorama");
            References<Panorama, Node>(e, DeleteBehavior.NoAction, nameof(Panorama.NodeId));
            References<Panorama, Node>(e, DeleteBehavior.NoAction, nameof(Panorama.LevelId));
            e.Property(x => x.Position).HasColumnType("geometry(Point, 4326)");
            e.Property(x => x.ZRelM).HasDefaultValue(1.6f).ValueGeneratedNever();
            e.Property(x => x.PrivacyStatus).HasDefaultValue(PrivacyStatus.Pending).ValueGeneratedNever();
            e.Property(x => x.PrivacyRegions).HasDefaultValueSql("'[]'::jsonb");
            e.HasIndex(x => x.Position).HasMethod("gist");
            e.Property(x => x.CreatedAt).HasDefaultValueSql("now()").ValueGeneratedNever();
            e.HasIndex(x => x.PrivacyStatus);
            e.ToTable(t => t.HasCheckConstraint("panorama_privacy_status",
                "privacy_status IN ('pending', 'review', 'rendering', 'approved', 'failed')"));
        });

        modelBuilder.Entity<PanoramaLink>(e =>
        {
            e.ToTable("panorama_link");
            e.HasKey(x => new { x.FromId, x.ToId });
            References<PanoramaLink, Panorama>(e, DeleteBehavior.Cascade, nameof(PanoramaLink.FromId));
            References<PanoramaLink, Panorama>(e, DeleteBehavior.Cascade, nameof(PanoramaLink.ToId));
        });

        modelBuilder.Entity<ModelAsset>(e =>
        {
            e.ToTable("model_asset");
            References<ModelAsset, Site>(e, DeleteBehavior.Cascade, nameof(ModelAsset.SiteId));
            References<ModelAsset, Node>(e, DeleteBehavior.NoAction, nameof(ModelAsset.NodeId));
            e.Property(x => x.Segmented).HasDefaultValue(false).ValueGeneratedNever();
            e.Property(x => x.Attrs).HasDefaultValueSql("'{}'::jsonb");
            e.Property(x => x.Status).HasDefaultValue(ModelStatus.Ready).ValueGeneratedNever();
            e.HasIndex(x => x.Status).HasFilter("status = 'converting'").HasDatabaseName("model_asset_converting");
            e.ToTable(t =>
            {
                t.HasCheckConstraint("model_asset_kind", "kind IN ('gltf', '3dtiles')");
                t.HasCheckConstraint("model_asset_status", "status IN ('ready', 'converting', 'failed')");
            });
        });

        modelBuilder.Entity<Floorplan>(e =>
        {
            e.ToTable("floorplan");
            References<Floorplan, Node>(e, DeleteBehavior.Cascade, nameof(Floorplan.LevelId));
            e.Property(x => x.WmsParams).HasDefaultValueSql("'{}'::jsonb");
            e.Property(x => x.Attrs).HasDefaultValueSql("'{}'::jsonb");
            e.Property(x => x.Opacity).HasDefaultValue(1f).ValueGeneratedNever();
            e.Property(x => x.SortOrder).HasDefaultValue(0).ValueGeneratedNever();
            e.ToTable(t => t.HasCheckConstraint("floorplan_source",
                "source = 'mapmanager' OR (source = 'external_wms' AND external_url IS NOT NULL)"));
        });

        modelBuilder.Entity<MapLayer>(e =>
        {
            e.ToTable("map_layer");
            e.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Cascade);
            References<MapLayer, Site>(e, DeleteBehavior.Cascade, nameof(MapLayer.SiteId));
            e.Property(x => x.WmsParams).HasDefaultValueSql("'{}'::jsonb");
            e.Property(x => x.VisibleDefault).HasDefaultValue(true).ValueGeneratedNever();
            e.Property(x => x.SortOrder).HasDefaultValue(0).ValueGeneratedNever();
            e.ToTable(t => t.HasCheckConstraint("map_layer_source",
                "source = 'mapmanager' OR (source = 'external_wms' AND external_url IS NOT NULL)"));
        });

        modelBuilder.Entity<Anchor>(e =>
        {
            e.ToTable("anchor");
            References<Anchor, Node>(e, DeleteBehavior.Cascade, nameof(Anchor.NodeId));
            References<Anchor, ModelAsset>(e, DeleteBehavior.Cascade, nameof(Anchor.ModelAssetId));
            References<Anchor, Panorama>(e, DeleteBehavior.Cascade, nameof(Anchor.PanoramaId));
            References<Anchor, Node>(e, DeleteBehavior.NoAction, nameof(Anchor.LevelId));
            e.Property(x => x.Footprint).HasColumnType("geometry(Polygon, 4326)");
            e.Property(x => x.ZRelM).HasDefaultValue(0f).ValueGeneratedNever();
            e.Property(x => x.Position).HasColumnType("geometry(Point, 4326)");
            e.Property(x => x.Attrs).HasDefaultValueSql("'{}'::jsonb");
            e.HasIndex(x => new { x.ModelAssetId, x.FeatureRef }).HasDatabaseName("anchor_feature");
            // One element, one appearance (user decision): one info point per panorama, one point per node.
            e.HasIndex(x => new { x.NodeId, x.PanoramaId }).IsUnique().HasFilter("kind = 'panorama_point'").HasDatabaseName("anchor_one_info_point_per_panorama");
            e.HasIndex(x => x.NodeId).IsUnique().HasFilter("kind = 'point'").HasDatabaseName("anchor_one_point");
            e.HasIndex(x => x.Position).HasMethod("gist");
            // Spec 8.2, with explicit IS NOT NULL: a comparison with NULL is NULL, and a CHECK that evaluates to
            // NULL passes – without them a box without sizes or a prism without z_max would be accepted.
            e.ToTable(t => t.HasCheckConstraint("anchor_kind_fields", """
                (kind = 'model_feature'  AND model_asset_id IS NOT NULL AND feature_ref IS NOT NULL) OR
                (kind = 'panorama_point' AND panorama_id IS NOT NULL AND yaw_deg IS NOT NULL AND pitch_deg IS NOT NULL) OR
                (kind = 'region' AND region_shape = 'box'
                                 AND size_x IS NOT NULL AND size_y IS NOT NULL AND size_z IS NOT NULL
                                 AND size_x > 0 AND size_y > 0 AND size_z > 0 AND heading_deg IS NOT NULL) OR
                (kind = 'region' AND region_shape = 'prism'
                                 AND footprint IS NOT NULL AND z_min IS NOT NULL AND z_max IS NOT NULL AND z_max > z_min) OR
                (kind = 'point')
                """));
        });

        modelBuilder.Entity<DrawLayer>(e =>
        {
            e.ToTable("draw_layer");
            References<DrawLayer, Site>(e, DeleteBehavior.Cascade, nameof(DrawLayer.SiteId));
            References<DrawLayer, Node>(e, DeleteBehavior.Cascade, nameof(DrawLayer.LevelId));
            e.Property(x => x.Structure).HasDefaultValueSql("'{}'::jsonb");
            e.Property(x => x.Style).HasDefaultValueSql("'{}'::jsonb");
            e.Property(x => x.SortOrder).HasDefaultValue(0).ValueGeneratedNever();
            e.Property(x => x.CreatedAt).HasDefaultValueSql("now()").ValueGeneratedNever();
            e.ToTable(t => t.HasCheckConstraint("draw_layer_geometry_type",
                "geometry_type IN ('Point', 'MultiLineString', 'MultiPolygon')"));
        });

        modelBuilder.Entity<DrawRecord>(e =>
        {
            e.ToTable("draw_record");
            References<DrawRecord, DrawLayer>(e, DeleteBehavior.Cascade, nameof(DrawRecord.LayerId));
            // ON DELETE SET NULL cannot be composite (it would clear organization_id too): plain key to the node.
            e.HasOne<Node>().WithMany().HasForeignKey(x => x.NodeId).OnDelete(DeleteBehavior.SetNull);
            e.Property(x => x.Attrs).HasDefaultValueSql("'{}'::jsonb");
            e.Property(x => x.Version).HasDefaultValue(1).ValueGeneratedNever();
            e.Property(x => x.ModifiedAt).HasDefaultValueSql("now()").ValueGeneratedNever();
        });

        modelBuilder.Entity<DrawGeometry>(e =>
        {
            e.ToTable("draw_geometry");
            References<DrawGeometry, DrawRecord>(e, DeleteBehavior.Cascade, nameof(DrawGeometry.RecordId));
            References<DrawGeometry, DrawLayer>(e, DeleteBehavior.Cascade, nameof(DrawGeometry.LayerId));
            e.Property(x => x.Geom).HasColumnType("geometry(Geometry, 4326)");
            e.HasIndex(x => x.Geom).HasMethod("gist");
        });

        modelBuilder.Entity<ChangeEntry>(e =>
        {
            e.ToTable("change_entry");
            // Only the organization and the site: the history outlives the node and its anchors.
            e.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Cascade);
            References<ChangeEntry, Site>(e, DeleteBehavior.Cascade, nameof(ChangeEntry.SiteId));
            e.HasIndex(x => new { x.NodeId, x.ValidFrom });
            e.HasIndex(x => new { x.Entity, x.EntityId });
            e.ToTable(t =>
            {
                t.HasCheckConstraint("change_entry_entity", "entity IN ('node', 'anchor')");
                t.HasCheckConstraint("change_entry_action", "action IN ('finalize', 'create', 'update', 'delete')");
            });
        });

        // Organization (spec 3.1) and, for site-restricted users, the readable sites (spec 3.2): rows of other sites are
        // simply not found. Tables without a site column reach their site through the node, level or drawing layer.
        OrganizationOwned<UserSiteRole>(modelBuilder, x => true);
        OrganizationOwned<Site>(modelBuilder, x => ReadableSites == null || ReadableSites.Contains(x.Id));
        OrganizationOwned<Node>(modelBuilder, x => ReadableSites == null || ReadableSites.Contains(x.SiteId));
        OrganizationOwned<ModelAsset>(modelBuilder, x => ReadableSites == null || ReadableSites.Contains(x.SiteId));
        OrganizationOwned<DrawLayer>(modelBuilder, x => ReadableSites == null || ReadableSites.Contains(x.SiteId));
        OrganizationOwned<ChangeEntry>(modelBuilder, x => ReadableSites == null || ReadableSites.Contains(x.SiteId));
        OrganizationOwned<MapLayer>(modelBuilder, x => ReadableSites == null || x.SiteId == null || ReadableSites.Contains(x.SiteId.Value));
        OrganizationOwned<NodeRelation>(modelBuilder,
            x => ReadableSites == null || Set<Node>().Any(n => n.Id == x.FromId && ReadableSites.Contains(n.SiteId)));
        OrganizationOwned<Panorama>(modelBuilder,
            x => ReadableSites == null || Set<Node>().Any(n => n.Id == x.NodeId && ReadableSites.Contains(n.SiteId)));
        OrganizationOwned<Anchor>(modelBuilder,
            x => ReadableSites == null || Set<Node>().Any(n => n.Id == x.NodeId && ReadableSites.Contains(n.SiteId)));
        OrganizationOwned<Floorplan>(modelBuilder,
            x => ReadableSites == null || Set<Node>().Any(n => n.Id == x.LevelId && ReadableSites.Contains(n.SiteId)));
        OrganizationOwned<PanoramaLink>(modelBuilder,
            x => ReadableSites == null || Set<Panorama>().Any(p => p.Id == x.FromId && Set<Node>().Any(n => n.Id == p.NodeId && ReadableSites.Contains(n.SiteId))));
        OrganizationOwned<DrawRecord>(modelBuilder,
            x => ReadableSites == null || Set<DrawLayer>().Any(l => l.Id == x.LayerId && ReadableSites.Contains(l.SiteId)));
        OrganizationOwned<DrawGeometry>(modelBuilder,
            x => ReadableSites == null || Set<DrawLayer>().Any(l => l.Id == x.LayerId && ReadableSites.Contains(l.SiteId)));
    }

    /// <summary>Sites the signed-in user may see (null: all of the organization), evaluated per query.</summary>
    private IReadOnlyList<long>? ReadableSites => currentUser?.ReadableSiteIds;

    /// <summary>The global filter of an organization-owned entity: its organization and the site condition.</summary>
    private void OrganizationOwned<T>(ModelBuilder modelBuilder, Expression<Func<T, bool>> siteCondition) where T : class, IOrganizationOwned
    {
        Expression<Func<T, bool>> organization = x => x.OrganizationId == CurrentOrganizationId;
        var parameter = organization.Parameters[0];
        var site = new ParameterReplacer(siteCondition.Parameters[0], parameter).Visit(siteCondition.Body);
        modelBuilder.Entity<T>().HasQueryFilter(Expression.Lambda<Func<T, bool>>(Expression.AndAlso(organization.Body, site), parameter));
    }

    private sealed class ParameterReplacer(ParameterExpression from, ParameterExpression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : base.VisitParameter(node);
    }

    /// <summary>
    /// Composite foreign key (<paramref name="column"/>, organization_id) → principal (id, organization_id): the
    /// referenced row is guaranteed to be in the same organization.
    /// </summary>
    private static void References<TDependent, TPrincipal>(EntityTypeBuilder<TDependent> e, DeleteBehavior onDelete, string column)
        where TDependent : class, IOrganizationOwned
        where TPrincipal : class, IOrganizationOwned =>
        e.HasOne<TPrincipal>().WithMany()
            .HasForeignKey(column, nameof(IOrganizationOwned.OrganizationId))
            .HasPrincipalKey("Id", nameof(IOrganizationOwned.OrganizationId))
            .OnDelete(onDelete);

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        StampOrganization();
        CheckEditableSitesAsync(CancellationToken.None).GetAwaiter().GetResult();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        StampOrganization();
        await CheckEditableSitesAsync(cancellationToken);
        var recorded = await RecordChangesAsync(cancellationToken);
        if (recorded.Count == 0) return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);

        // New anchors get their id on insert: the history rows learn it in a second save, in the same transaction.
        var own = Database.CurrentTransaction is null ? await Database.BeginTransactionAsync(cancellationToken) : null;
        try
        {
            StampOrganization();
            var count = await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
            foreach (var (entry, entity) in recorded)
                if (entity is Anchor anchor) entry.EntityId = anchor.Id;
            await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
            if (own is not null) await own.CommitAsync(cancellationToken);
            return count;
        }
        finally
        {
            if (own is not null) await own.DisposeAsync();
        }
    }

    /// <summary>
    /// Site-level write permission (spec 3.2), centrally: every added, changed or deleted row must belong to a site the
    /// user may edit. Bulk ExecuteUpdate / ExecuteDelete bypass this; their services check the site explicitly.
    /// </summary>
    private async Task CheckEditableSitesAsync(CancellationToken ct)
    {
        if (currentUser?.EditableSiteIds is not { } editable) return;
        var entries = ChangeTracker.Entries<IOrganizationOwned>()
            .Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted && e.Entity is not UserSiteRole)
            .Select(e => e.Entity).ToList();
        if (entries.Count == 0) return;

        long? Direct(IOrganizationOwned e) => e switch
        {
            Site s => s.Id,
            Node n => n.SiteId,
            ModelAsset m => m.SiteId,
            DrawLayer l => l.SiteId,
            MapLayer l => l.SiteId,
            _ => null,
        };
        var nodeIds = entries.Select(e => e switch
        {
            Panorama p => p.NodeId, Anchor a => a.NodeId, Floorplan f => f.LevelId, NodeRelation r => r.FromId, _ => (long?)null,
        }).OfType<long>().ToList();
        var panoramaIds = entries.OfType<PanoramaLink>().Select(l => l.FromId).ToList();
        var layerIds = entries.Select(e => e switch { DrawRecord r => r.LayerId, DrawGeometry g => g.LayerId, _ => (long?)null }).OfType<long>().ToList();

        var sites = entries.Select(Direct).OfType<long>().ToHashSet();
        if (nodeIds.Count > 0)
            sites.UnionWith(await Set<Node>().IgnoreQueryFilters().Where(n => nodeIds.Contains(n.Id)).Select(n => n.SiteId).ToListAsync(ct));
        if (panoramaIds.Count > 0)
            sites.UnionWith(await Set<Panorama>().IgnoreQueryFilters().Where(p => panoramaIds.Contains(p.Id))
                .Join(Set<Node>().IgnoreQueryFilters(), p => p.NodeId, n => n.Id, (p, n) => n.SiteId).ToListAsync(ct));
        if (layerIds.Count > 0)
            sites.UnionWith(await Set<DrawLayer>().IgnoreQueryFilters().Where(l => layerIds.Contains(l.Id)).Select(l => l.SiteId).ToListAsync(ct));
        // An organization-wide map layer (no site) is organization admin business (its endpoints say so).
        if (sites.Any(s => !editable.Contains(s)))
            throw new AppException(StatusCodes.Status403Forbidden, "site.read_only", "You have read-only access to this site.");
    }

    /// <summary>New organization-owned rows get the current organization; another organization's id is a bug.</summary>
    private void StampOrganization()
    {
        foreach (var entry in ChangeTracker.Entries<IOrganizationOwned>().Where(e => e.State == EntityState.Added))
        {
            if (entry.Entity.OrganizationId == 0)
                entry.Entity.OrganizationId = CurrentOrganizationId
                    ?? throw new InvalidOperationException("Organization data cannot be saved from a request without an organization.");
            else if (CurrentOrganizationId is { } current && entry.Entity.OrganizationId != current)
                throw new InvalidOperationException("Organization data cannot be saved into another organization.");
        }
    }
}
