using System.Text.Json;
using System.Text.RegularExpressions;
using Api.Data;
using Api.Model;
using Microsoft.EntityFrameworkCore;

namespace Api.Services.Hierarchy;

public record NodeTypeCreate(string Code, string Name, string Aspect, bool? IsLevel, JsonElement? AttrSchema);

/// <summary>Partial update; null = not sent (<see cref="AttrSchemaSet"/> distinguishes removing the schema).</summary>
public record NodeTypeUpdate(string? Name = null, JsonElement? AttrSchema = null, bool AttrSchemaSet = false);

/// <summary>
/// Node types (FR-H2): the shared base types (no organization, read only) plus the organization's own ones.
/// The aspect and the level flag are fixed after creation, because existing trees depend on them.
/// </summary>
public partial class NodeTypeService(AppDbContext db, Auth.CurrentUser currentUser)
{
    [GeneratedRegex("^[a-z][a-z0-9_]{1,63}$")]
    private static partial Regex CodeRegex();

    public async Task<List<NodeType>> ListAsync(CancellationToken ct) =>
        await db.NodeTypes.AsNoTracking().OrderBy(t => t.Aspect).ThenBy(t => t.OrganizationId != null).ThenBy(t => t.Name).ToListAsync(ct);

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
