using System.Reflection;

namespace Api.Data;

/// <summary>SQL scripts from db/sql, embedded into the assembly, for migrations.</summary>
public static class SqlScripts
{
    public static string Load(string fileName)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"Sql.{fileName}")
            ?? throw new InvalidOperationException($"Embedded SQL script not found: {fileName}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
