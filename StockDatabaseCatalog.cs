using System.IO;
using System.IO.Compression;
using System.Reflection;
using Microsoft.Data.Sqlite;

namespace FH6LocalCryptoTool;

/// <summary>Immutable clean references, selected by the exact database stamp, never by age/name.</summary>
public static class StockDatabaseCatalog
{
    private static readonly Lazy<IReadOnlyList<string>> References = new(ExtractReferences);

    public static string Version(string sqlitePath)
    {
        using var db = Open(sqlitePath);
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT database_version FROM VersionInfo";
        using var reader = cmd.ExecuteReader();
        if (!reader.Read() || reader.IsDBNull(0))
            throw new InvalidDataException("The GameDB has no database version stamp.");
        string version = Convert.ToString(reader.GetValue(0), System.Globalization.CultureInfo.InvariantCulture)!;
        if (reader.Read()) throw new InvalidDataException("The GameDB has multiple version stamps; a baseline cannot be selected safely.");
        return version;
    }

    public static string? Find(string sqlitePath, IEnumerable<string>? references = null)
    {
        string version = Version(sqlitePath);
        var matches = (references ?? References.Value).Where(p => Version(p) == version).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (matches.Count > 1)
            throw new InvalidDataException($"Multiple embedded clean references have stamp {version}. Choose an explicit clean baseline.");
        return matches.SingleOrDefault();
    }

    private static IReadOnlyList<string> ExtractReferences()
    {
        var assembly = Assembly.GetExecutingAssembly();
        string directory = Path.Combine(Path.GetTempPath(), "FH6LocalCryptoTool", "stockrefs-" + Environment.ProcessId);
        Directory.CreateDirectory(directory);
        var paths = new List<string>();
        foreach (string name in assembly.GetManifestResourceNames().Where(n =>
                     n.StartsWith("FH6LocalCryptoTool.gamedbRC.stock", StringComparison.Ordinal) &&
                     n.EndsWith(".sqlite.gz", StringComparison.Ordinal)))
        {
            string path = Path.Combine(directory, name + ".sqlite");
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var gzip = new GZipStream(stream, CompressionMode.Decompress);
            using (var output = File.Create(path)) gzip.CopyTo(output);
            _ = Version(path);
            paths.Add(path);
        }
        return paths;
    }

    private static SqliteConnection Open(string path)
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        db.Open(); return db;
    }
}
