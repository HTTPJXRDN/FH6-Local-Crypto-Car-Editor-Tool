using System;
using System.IO;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace FH6LocalCryptoTool;

/// <summary>
/// Converts an encrypted GameDB to a private temporary SQLite file for the
/// existing editor/merge engine, and writes a new encrypted GameDB afterward.
/// Callers own only their outputs; source .slt and .sqlite files are read-only.
/// </summary>
public static class GameDbSqliteBridge
{
    private const string TempPrefix = "fh6_mod_studio_slt_";
    public static bool IsSlt(string path) =>
        Path.GetExtension(path).Equals(".slt", StringComparison.OrdinalIgnoreCase);

    public sealed class MaterializedDatabase : IDisposable
    {
        private readonly bool _ownsFile;
        public string SqlitePath { get; }
        public string? TemplateSlt { get; }
        public string? SourceHash { get; }

        internal MaterializedDatabase(string path, string? templateSlt, bool ownsFile, string? sourceHash = null)
        {
            SqlitePath = path;
            TemplateSlt = templateSlt;
            _ownsFile = ownsFile;
            SourceHash = sourceHash;
        }

        public void VerifySourceUnchanged()
        {
            if (TemplateSlt is not null && SourceHash is not null)
                VerifySourceHash(TemplateSlt, SourceHash);
        }

        public void Dispose()
        {
            if (!_ownsFile) return;
            foreach (string path in new[] { SqlitePath, SqlitePath + "-wal", SqlitePath + "-shm", SqlitePath + "-journal" })
                try { File.Delete(path); } catch { /* cleanup is best effort */ }
        }
    }

    public static MaterializedDatabase Materialize(string inputPath)
    {
        inputPath = Path.GetFullPath(inputPath);
        if (!File.Exists(inputPath)) throw new FileNotFoundException("Database not found.", inputPath);
        if (!IsSlt(inputPath))
        {
            if (!LooksLikeSqliteFile(inputPath))
                throw new InvalidDataException("Choose a decrypted SQLite database or an encrypted GameDB .slt.");
            return new MaterializedDatabase(inputPath, null, false);
        }

        string temp = Path.Combine(Path.GetTempPath(), TempPrefix + Guid.NewGuid().ToString("N") + ".sqlite");
        try
        {
            byte[] encrypted = File.ReadAllBytes(inputPath);
            string hash = Convert.ToHexString(SHA256.HashData(encrypted));
            byte[] sqlite = GameDb.Decrypt(encrypted, Fh6Keys.Get("GameDB").DataKey);
            if (!GameDb.LooksLikeSqlite(sqlite))
                throw new InvalidDataException("The .slt did not decrypt to a SQLite GameDB. Check the file and game version.");
            File.WriteAllBytes(temp, sqlite);
            CheckSqlite(temp);
            return new MaterializedDatabase(temp, inputPath, true, hash);
        }
        catch
        {
            try { File.Delete(temp); } catch { }
            throw;
        }
    }

    public static void VerifySourceHash(string path, string expectedHash)
    {
        using var source = File.OpenRead(path);
        string currentHash = Convert.ToHexString(SHA256.HashData(source));
        if (!string.Equals(currentHash, expectedHash, StringComparison.Ordinal))
            throw new InvalidOperationException("The original .slt changed after it was loaded. Reload it before exporting or merging.");
    }

    public static void EncryptSnapshot(string sqlitePath, string templateSlt, string outputSlt)
    {
        sqlitePath = Path.GetFullPath(sqlitePath);
        templateSlt = Path.GetFullPath(templateSlt);
        outputSlt = Path.GetFullPath(outputSlt);
        if (!IsSlt(outputSlt))
            throw new InvalidOperationException("Encrypted GameDB output must use the .slt extension.");
        if (string.Equals(outputSlt, templateSlt, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(outputSlt, sqlitePath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Choose a new output path; source files are never overwritten.");
        if (File.Exists(outputSlt))
            throw new IOException("The output .slt already exists. Choose a new filename.");
        CheckSqlite(sqlitePath);

        byte[] sqlite = File.ReadAllBytes(sqlitePath);
        byte[] template = File.ReadAllBytes(templateSlt);
        if (!GameDb.LooksLikeSqlite(GameDb.Decrypt(template, Fh6Keys.Get("GameDB").DataKey)))
            throw new InvalidDataException("The selected .slt template is not a readable GameDB.");
        byte[] encrypted = GameDb.Encrypt(sqlite, template, Fh6Keys.Get("GameDB").DataKey);
        byte[] roundTrip = GameDb.Decrypt(encrypted, Fh6Keys.Get("GameDB").DataKey);
        if (roundTrip.Length < sqlite.Length || !roundTrip.AsSpan(0, sqlite.Length).SequenceEqual(sqlite))
            throw new InvalidDataException("Encrypted output did not reproduce the edited SQLite database.");

        string partial = outputSlt + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            File.WriteAllBytes(partial, encrypted);
            File.Move(partial, outputSlt);
        }
        finally
        {
            try { File.Delete(partial); } catch { }
        }
    }

    private static bool LooksLikeSqliteFile(string path)
    {
        using var stream = File.OpenRead(path);
        Span<byte> header = stackalloc byte[16];
        return stream.Read(header) == 16 &&
               header.SequenceEqual("SQLite format 3\0"u8);
    }

    private static void CheckSqlite(string path)
    {
        if (!LooksLikeSqliteFile(path))
            throw new InvalidDataException("SQLite header not found.");
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false
        }.ToString());
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "PRAGMA quick_check";
        if (!string.Equals(Convert.ToString(cmd.ExecuteScalar()), "ok", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("SQLite integrity check failed.");
    }
}
