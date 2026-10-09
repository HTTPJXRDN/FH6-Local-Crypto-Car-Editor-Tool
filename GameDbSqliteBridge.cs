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
    private const string TempPrefix = "forza_mod_studio_slt_";
    public static bool IsSlt(string path) =>
        Path.GetExtension(path).Equals(".slt", StringComparison.OrdinalIgnoreCase);

    public sealed class MaterializedDatabase : IDisposable
    {
        private readonly bool _ownsFile;
        public string SqlitePath { get; }
        public string? TemplateSlt { get; }
        public string? SourceHash { get; }
        public GameDbContainerFormat.Kind Format { get; }

        internal MaterializedDatabase(string path, string? templateSlt, bool ownsFile, string? sourceHash = null,
            GameDbContainerFormat.Kind format = GameDbContainerFormat.Kind.Sqlite)
        {
            SqlitePath = path;
            TemplateSlt = templateSlt;
            _ownsFile = ownsFile;
            SourceHash = sourceHash;
            Format = format;
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

    public static MaterializedDatabase Materialize(string inputPath, bool allowMotorsport = false)
    {
        inputPath = Path.GetFullPath(inputPath);
        if (!File.Exists(inputPath)) throw new FileNotFoundException("Database not found.", inputPath);
        // A magic string alone is not a SQLite database. This also permits a
        // genuine plaintext database named .slt without treating it as encrypted.
        if (IsSqliteDatabase(inputPath))
            return new MaterializedDatabase(inputPath, null, false);
        if (!IsSlt(inputPath) && GameDbContainerFormat.Detect(inputPath) is not
            (GameDbContainerFormat.Kind.Fh6Aes36 or GameDbContainerFormat.Kind.ForzaMotorsportTransformIt32))
        {
            throw new InvalidDataException("Choose a valid decrypted SQLite database or an encrypted GameDB .slt.");
        }

        string temp = Path.Combine(FH6LocalCryptoTool.TempWorkspace.Root, TempPrefix + Guid.NewGuid().ToString("N") + ".sqlite");
        try
        {
            byte[] encrypted = File.ReadAllBytes(inputPath);
            string hash = Convert.ToHexString(SHA256.HashData(encrypted));
            var format = GameDbContainerFormat.Detect(encrypted);
            if (format == GameDbContainerFormat.Kind.ForzaMotorsportTransformIt32 && !allowMotorsport)
                throw new NotSupportedException("Use the Crypto tab to decrypt Motorsport .slt files, then open the SQLite separately. Motorsport editor/merge integration is not enabled.");
            byte[] sqlite = format switch
            {
                GameDbContainerFormat.Kind.Fh6Aes36 =>
                    GameDb.Decrypt(encrypted, Fh6Keys.Get("GameDB").DataKey),
                GameDbContainerFormat.Kind.ForzaMotorsportTransformIt32 =>
                    MotorsportGameDb.Decrypt(encrypted),
                _ => throw new InvalidDataException(
                    "The .slt layout is not a recognized Forza GameDB container.")
            };
            if (!GameDb.LooksLikeSqlite(sqlite))
                throw new InvalidDataException(
                    $"The {GameDbContainerFormat.DisplayName(format)} did not decrypt to SQLite. " +
                    "The key or game build is not supported.");
            File.WriteAllBytes(temp, sqlite);
            CheckSqlite(temp);
            return new MaterializedDatabase(temp, inputPath, true, hash, format);
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

    public static void EncryptSnapshot(string sqlitePath, string templateSlt, string outputSlt, bool allowMotorsport = false)
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
        // Validate the entire decrypted template, not its outer magic string.
        // Both FH6 and FM go through the same payload/integrity validation.
        using var validatedTemplate = Materialize(templateSlt, allowMotorsport);
        var format = validatedTemplate.Format;
        if (format != GameDbContainerFormat.Kind.Fh6Aes36 && format != GameDbContainerFormat.Kind.ForzaMotorsportTransformIt32)
            throw new InvalidDataException("The selected .slt template is not a recognized encrypted GameDB.");
        bool motorsport = format == GameDbContainerFormat.Kind.ForzaMotorsportTransformIt32;
        if (motorsport && !allowMotorsport)
            throw new NotSupportedException("Use the Crypto tab for Motorsport re-encryption. Motorsport editor/merge integration is not enabled.");
        validatedTemplate.VerifySourceUnchanged();
        if (validatedTemplate.SourceHash != Convert.ToHexString(SHA256.HashData(template)))
            throw new InvalidOperationException("The selected .slt template changed during validation. Reload it before exporting.");
        byte[] encrypted = motorsport ? MotorsportGameDb.Encrypt(sqlite, template)
            : GameDb.Encrypt(sqlite, template, Fh6Keys.Get("GameDB").DataKey);
        byte[] roundTrip = motorsport ? MotorsportGameDb.Decrypt(encrypted)
            : GameDb.Decrypt(encrypted, Fh6Keys.Get("GameDB").DataKey);
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

    public static GameDbContainerFormat.Kind DetectFormat(string path)
    {
        using var database = Materialize(path, allowMotorsport: true);
        return database.Format;
    }

    public static bool IsSqliteDatabase(string path)
    {
        try
        {
            if (!LooksLikeSqliteFile(path)) return false;
            CheckSqlite(path);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SqliteException or InvalidDataException)
        {
            return false;
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
        using var result = cmd.ExecuteReader();
        if (!result.Read() || !string.Equals(result.GetString(0), "ok", StringComparison.OrdinalIgnoreCase) || result.Read())
            throw new InvalidDataException("SQLite integrity check failed.");
    }
}
