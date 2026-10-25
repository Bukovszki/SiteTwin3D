using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Api.Data;
using Api.Model;
using Microsoft.EntityFrameworkCore;

namespace Api.Services.Hierarchy;

public record NodeTypeCreate(string Code, string Name, string Aspect, bool? IsLevel, JsonElement? AttrSchema);

/// <summary>Partial update; null = not sent (<see cref="AttrSchemaSet"/> distinguishes removing the schema).</summary>
public record NodeTypeUpdate(string? Name = null, JsonElement? AttrSchema = null, bool AttrSchemaSet = false);

/// <summary>A type as the organization sees it: <see cref="Extension"/> = its own fields added to a shared base type.</summary>
public record NodeTypeView(NodeType Type, JsonDocument? Extension)
{
    /// <summary>The fields that apply: the type's own, then the organization's added ones.</summary>
    public JsonDocument? Schema => NodeTypeService.Merge(Type.AttrSchema, Extension);
}

/// <summary>
/// Node types (FR-H2): the shared base types (no organization, read only) plus the organization's own ones.
/// The aspect and the level flag are fixed after creation, because existing trees depend on them. An organization
/// may add its own fields to a shared base type (<see cref="NodeTypeExtension"/>, user decision); every check of a
/// record's technical data uses the merged schema (<see cref="SchemaOfAsync"/>).
/// </summary>
public partial class NodeTypeService(AppDbContext db, Auth.CurrentUser currentUser)
{
    [GeneratedRegex("^[a-z][a-z0-9_]{1,63}$")]
    private static partial Regex CodeRegex();

    public async Task<List<NodeTypeView>> ListAsync(CancellationToken ct)
    {
        var types = await db.NodeTypes.AsNoTracking().OrderBy(t => t.Aspect).ThenBy(t => t.OrganizationId != null).ThenBy(t => t.Name).ToListAsync(ct);
        var extensions = await db.NodeTypeExtensions.AsNoTracking().ToDictionaryAsync(x => x.NodeTypeId, x => x.AttrSchema, ct);
        return types.Select(t => new NodeTypeView(t, extensions.GetValueOrDefault(t.Id))).ToList();
    }

    public async Task<NodeTypeView> GetAsync(long id, CancellationToken ct)
    {
        var type = await db.NodeTypes.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, ct)
            ?? throw AppException.NotFound("node_type.not_found", $"Node type {id} not found.");
        var extension = await db.NodeTypeExtensions.AsNoTracking().Where(x => x.NodeTypeId == id).Select(x => x.AttrSchema).FirstOrDefaultAsync(ct);
        return new NodeTypeView(type, extension);
    }

    /// <summary>The schema a record of this type is checked against: the type's own plus the organization's fields.</summary>
    public async Task<JsonDocument?> SchemaOfAsync(NodeType type, CancellationToken ct)
    {
        if (type.OrganizationId is not null) return type.AttrSchema;
        var extension = await db.NodeTypeExtensions.AsNoTracking().Where(x => x.NodeTypeId == type.Id).Select(x => x.AttrSchema).FirstOrDefaultAsync(ct);
        return Merge(type.AttrSchema, extension);
    }

    /// <summary>
    /// The organization's own fields of a shared base type (null or no properties: removed). Their names must differ
    /// from the base type's fields; values already entered in a removed field stay in the records, hidden.
    /// </summary>
    public async Task<NodeTypeView> SetExtensionAsync(long id, JsonElement? schema, CancellationToken ct)
    {
        var type = await db.NodeTypes.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, ct)
            ?? throw AppException.NotFound("node_type.not_found", $"Node type {id} not found.");
        if (type.OrganizationId is not null)
            throw AppException.BadRequest("node_type.own_type", "An own type's fields are its schema (attrSchema), not an extension.");
        AttrValidator.ValidateSchema(schema);
        var properties = Properties(schema);
        var taken = PropertyNames(type.AttrSchema).Intersect(properties).ToList();
        if (taken.Count > 0)
            throw AppException.BadRequest("node_type.field_exists", $"The base type already has these fields: {string.Join(", ", taken)}.");

        var existing = await db.NodeTypeExtensions.FirstOrDefaultAsync(x => x.NodeTypeId == id, ct);
        if (properties.Count == 0)
        {
            if (existing is not null) db.NodeTypeExtensions.Remove(existing);
        }
        else if (existing is null)
            db.NodeTypeExtensions.Add(new NodeTypeExtension { NodeTypeId = id, AttrSchema = SchemaDocument(schema)! });
        else
            existing.AttrSchema = SchemaDocument(schema)!;
        await db.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    /// <summary>The base schema with the added fields after its own (properties and required merged).</summary>
    public static JsonDocument? Merge(JsonDocument? baseSchema, JsonDocument? extension)
    {
        if (extension is null || Properties(extension.RootElement).Count == 0) return baseSchema;
        var merged = baseSchema is null
            ? new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() }
            : JsonNode.Parse(baseSchema.RootElement.GetRawText())!.AsObject();
        if (merged["properties"] is not JsonObject properties)
            merged["properties"] = properties = new JsonObject();
        var added = JsonNode.Parse(extension.RootElement.GetRawText())!.AsObject();
        foreach (var (name, value) in (added["properties"] as JsonObject)!.ToList())
            properties[name] = value?.DeepClone();
        var required = (merged["required"] as JsonArray)?.Select(r => r!.GetValue<string>()).ToList() ?? [];
        foreach (var name in (added["required"] as JsonArray)?.Select(r => r!.GetValue<string>()) ?? [])
            if (!required.Contains(name)) required.Add(name);
        if (required.Count > 0) merged["required"] = new JsonArray(required.Select(r => (JsonNode)JsonValue.Create(r)!).ToArray());
        return JsonDocument.Parse(merged.ToJsonString());
    }

    private static List<string> Properties(JsonElement? schema) =>
        schema is { ValueKind: JsonValueKind.Object } s && s.TryGetProperty("properties", out var p) && p.ValueKind == JsonValueKind.Object
            ? p.EnumerateObject().Select(x => x.Name).ToList()
            : [];

    private static List<string> PropertyNames(JsonDocument? schema) => Properties(schema?.RootElement);

    public async Task<NodeType> CreateAsync(NodeTypeCreate request, CancellationToken ct)
    {
        var code = (request.Code ?? "").Trim();
        if (!CodeRegex().IsMatch(code))
            throw AppException.BadRequest("node_type.invalid_code", "Code: 2–64 characters, lowercase letter first, then lowercase letters, digits or _.");
        if (request.Aspect is not (Aspects.Location or Aspects.Function))
            throw AppException.BadRequest("node_type.invalid_aspect", "Aspect: location or function.");
        // Visible types are the shared ones and the organization's own: neither may have this code.
        if (await db.NodeTypes.AnyAsync(t => t.Code == code, ct))
            throw AppException.Conflict("node_type.code_exists", $"A node type with code '{code}' already exists.");
        AttrValidator.ValidateSchema(request.AttrSchema);

        var type = new NodeType
        {
            OrganizationId = currentUser.OrganizationId ?? throw new InvalidOperationException("No organization in the request."),
            Code = code,
            Name = ValidName(request.Name),
            Aspect = request.Aspect,
            IsLevel = request.IsLevel ?? false,
            AttrSchema = SchemaDocument(request.AttrSchema),
        };
        db.NodeTypes.Add(type);
        await db.SaveChangesAsync(ct);
        return type;
    }

    public async Task<NodeType> UpdateAsync(long id, NodeTypeUpdate request, CancellationToken ct)
    {
        var type = await OwnTypeAsync(id, ct);
        if (request.Name is not null) type.Name = ValidName(request.Name);
        if (request.AttrSchemaSet)
        {
            AttrValidator.ValidateSchema(request.AttrSchema);
            type.AttrSchema = SchemaDocument(request.AttrSchema);
        }
        await db.SaveChangesAsync(ct);
        return type;
    }

    public async Task DeleteAsync(long id, CancellationToken ct)
    {
        var type = await OwnTypeAsync(id, ct);
        if (await db.Nodes.AnyAsync(n => n.TypeId == id, ct))
            throw AppException.Conflict("node_type.in_use", "The node type is in use.");
        db.NodeTypes.Remove(type);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Only the organization's own types can be changed; shared base types are read only.</summary>
    private async Task<NodeType> OwnTypeAsync(long id, CancellationToken ct)
    {
        var type = await db.NodeTypes.FirstOrDefaultAsync(t => t.Id == id, ct)
            ?? throw AppException.NotFound("node_type.not_found", $"Node type {id} not found.");
        if (type.OrganizationId is null)
            throw new AppException(StatusCodes.Status403Forbidden, "node_type.shared_read_only", "Shared base types cannot be changed.");
        return type;
    }

    private static string ValidName(string? name)
    {
        var trimmed = (name ?? "").Trim();
        if (trimmed.Length is 0 or > 200)
            throw AppException.BadRequest("node_type.invalid_name", "Name: 1–200 characters.");
        return trimmed;
    }

    private static JsonDocument? SchemaDocument(JsonElement? schema) =>
        schema is { ValueKind: JsonValueKind.Object } s ? JsonSerializer.SerializeToDocument(s) : null;
}
