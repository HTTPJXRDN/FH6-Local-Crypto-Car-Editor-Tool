using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace FH6LocalCryptoTool.DbBrowser;

public sealed record BrowserObject(string Name, string Type, string TableName, string Sql) { public override string ToString() => Name; }
public sealed record BrowserColumn(string Name, string Type, bool NotNull, string? Default, int KeyOrder, int Hidden) { public override string ToString() => Name; }
public sealed record BrowserRow(object[] Values, object[] Identity);
public sealed record BrowserPage(IReadOnlyList<BrowserColumn> Columns, IReadOnlyList<BrowserRow> Rows, long Count, bool Editable);
public sealed record BrowserValue(string Kind, string Text)
{
    public object Parse() => Kind switch
    {
        "NULL" => DBNull.Value,
        "TEXT" => Text,
        "INTEGER" => long.TryParse(Text.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var i)
            ? i : throw new FormatException("Enter a whole number within SQLite's signed 64-bit range."),
        "REAL" => NumericText.TryParseDouble(Text, out var d) ? d
            : throw new FormatException("Enter a finite decimal number, using a dot or comma (not thousands separators)."),
        "BLOB" => Convert.FromHexString(string.Concat(Text.Where(c => !char.IsWhiteSpace(c)))),
        _ => throw new FormatException("Choose a SQLite value type.")
    };

    public static BrowserValue From(object value) => value switch
    {
        DBNull => new("NULL", ""),
        byte[] b => new("BLOB", Convert.ToHexString(b)),
        long i => new("INTEGER", i.ToString(CultureInfo.InvariantCulture)),
        double d => new("REAL", d.ToString("R", CultureInfo.InvariantCulture)),
        _ => new("TEXT", Convert.ToString(value, CultureInfo.InvariantCulture) ?? "")
    };

    public static BrowserValue Inline(BrowserColumn column, object original, string text)
    {
        string kind = From(original).Kind;
        if (kind == "NULL") {
            string type = column.Type.ToUpperInvariant();
            kind = type.Contains("INT") ? "INTEGER" :
                type.Contains("CHAR") || type.Contains("CLOB") || type.Contains("TEXT") ? "TEXT" :
                type.Contains("REAL") || type.Contains("FLOA") || type.Contains("DOUB") ? "REAL" :
                type.Contains("BLOB") ? "BLOB" : type.Length == 0 ? "TEXT" : "REAL";
        }
        return new(kind, text);
    }
}

/// <summary>All edits occur on an owned SQLite snapshot, never on an input file.</summary>
public sealed partial class BrowserDatabase : IDisposable
{
    readonly SqliteConnection _db;
    readonly string _workPath;
    readonly string _checkpointPath;
    readonly string? _sourceHash;
    bool _pending;
    bool _disposed;
    public string SourcePath { get; }
    public bool Pending => _pending;
    public bool HasChanges { get; private set; }
    public const int PageSize = 250;
    public const int SqlResultLimit = 1000;
    public static string Quote(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";

    BrowserDatabase(string source, string working, string? hash, SqliteConnection db)
    {
        SourcePath = source; _workPath = working; _checkpointPath = working + ".checkpoint"; _sourceHash = hash; _db = db;
        Execute("PRAGMA foreign_keys=OFF"); // GameDB contains known stock FK warnings; report, do not silently repair.
        SaveCheckpoint();
    }

    public static BrowserDatabase Open(string path)
    {
        path = Path.GetFullPath(path);
        string working = Path.Combine(FH6LocalCryptoTool.TempWorkspace.Root, "fh6_db_browser_" + Guid.NewGuid().ToString("N") + ".sqlite");
        SqliteConnection? db = null;
        try
        {
            using var materialized = GameDbSqliteBridge.Materialize(path);
            using var input = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = materialized.SqlitePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            db = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = working, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString());
            input.Open(); db.Open(); input.BackupDatabase(db);
            using var check = db.CreateCommand(); check.CommandText = "PRAGMA quick_check";
            if (!string.Equals(Convert.ToString(check.ExecuteScalar()), "ok", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Database failed SQLite quick_check.");
            materialized.VerifySourceUnchanged();
            return new BrowserDatabase(path, working, materialized.SourceHash, db);
        }
        catch { db?.Dispose(); DeleteOwned(working); DeleteOwned(working + ".checkpoint"); throw; }
    }

    SqliteCommand Command(string sql)
    {
        var cmd = _db.CreateCommand(); cmd.CommandText = sql; cmd.CommandTimeout = 10; return cmd;
    }
    void Execute(string sql) { using var cmd = Command(sql); cmd.ExecuteNonQuery(); }
    long Scalar(string sql) { using var cmd = Command(sql); return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture); }

    public IReadOnlyList<BrowserObject> Objects()
    {
        using var cmd = Command("SELECT name,type,tbl_name,coalesce(sql,'') FROM sqlite_schema WHERE name NOT LIKE 'sqlite_%' ORDER BY type,name");
        using var reader = cmd.ExecuteReader(); var result = new List<BrowserObject>();
        while (reader.Read()) result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        return result;
    }
    public IReadOnlyList<BrowserColumn> Columns(string table)
    {
        using var cmd = Command("PRAGMA table_xinfo(" + Quote(table) + ")"); using var r = cmd.ExecuteReader();
        var result = new List<BrowserColumn>();
        while (r.Read()) result.Add(new(r.GetString(1), r.GetString(2), r.GetInt64(3) != 0,
            r.IsDBNull(4) ? null : r.GetString(4), r.GetInt32(5), r.GetInt32(6)));
        return result;
    }
    (string[] Names, bool RowId) Identity(BrowserObject obj, IReadOnlyList<BrowserColumn> columns)
    {
        if (obj.Type != "table" || obj.Sql.Contains("CREATE VIRTUAL", StringComparison.OrdinalIgnoreCase)) return ([], false);
        // Query SQLite's authoritative table flags instead of parsing WITHOUT ROWID in SQL text.
        using var cmd = Command("SELECT wr FROM pragma_table_list WHERE schema='main' AND name=$name");
        cmd.Parameters.AddWithValue("$name", obj.Name);
        bool withoutRowid = Convert.ToInt64(cmd.ExecuteScalar() ?? 0) == 1;
        if (!withoutRowid)
            foreach (string name in new[] { "rowid", "_rowid_", "oid" })
                if (!columns.Any(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) return ([name], true);
        var keys = columns.Where(c => c.KeyOrder > 0).OrderBy(c => c.KeyOrder).ToArray();
        // Nullable non-integer primary keys are not unique row identities in ordinary SQLite tables.
        return withoutRowid || keys.All(c => c.NotNull) && keys.Length > 0
            ? (keys.Select(c => c.Name).ToArray(), false) : ([], false);
    }

    public BrowserPage Page(BrowserObject obj, int page, string search, string? sort, bool descending, CancellationToken cancel = default, IReadOnlyDictionary<string, string>? filters = null)
    {
        using var guard = Limit(cancel);
        var columns = Columns(obj.Name).Where(c => c.Hidden != 1).ToArray();
        var identity = Identity(obj, columns);
        if (columns.Length == 0) return new(columns, [], 0, false);
        var conditions = new List<string>(); var parameters = new Dictionary<string, object>();
        string escaped = search.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
        if (!string.IsNullOrEmpty(search)) {
            conditions.Add("(" + string.Join(" OR ", columns.Select(c => "CAST(" + Quote(c.Name) + " AS TEXT) LIKE $search ESCAPE '\\'")) + ")");
            parameters.Add("$search", "%" + escaped + "%");
        }
        if (filters != null) foreach (var filter in filters.Where(p => p.Value.Length > 0)) {
            if (!columns.Any(c => c.Name == filter.Key)) throw new InvalidOperationException("Unknown filter column: " + filter.Key);
            string name = Quote(filter.Key), text = filter.Value.Trim(), parameter = "$f" + parameters.Count;
            if (text.Equals("NULL", StringComparison.OrdinalIgnoreCase)) { conditions.Add(name + " IS NULL"); continue; }
            if (text.Equals("NOT NULL", StringComparison.OrdinalIgnoreCase)) { conditions.Add(name + " IS NOT NULL"); continue; }
            string? op = new[] { ">=", "<=", "<>", "!=", "=", ">", "<" }.FirstOrDefault(text.StartsWith);
            if (op != null) {
                string value = text[op.Length..].Trim();
                if (op is ">" or "<" or ">=" or "<=") {
                    if (!NumericText.TryParseDouble(value, out double number)) throw new FormatException("Numeric filters use e.g. >10 or <=0.05.");
                    conditions.Add(name + " " + op + " " + parameter); parameters.Add(parameter, number);
                } else { conditions.Add("CAST(" + name + " AS TEXT) " + op + " " + parameter); parameters.Add(parameter, value); }
            } else {
                conditions.Add("CAST(" + name + " AS TEXT) LIKE " + parameter + " ESCAPE '\\'");
                parameters.Add(parameter, "%" + text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%");
            }
        }
        string where = conditions.Count == 0 ? "" : " WHERE " + string.Join(" AND ", conditions);
        using var count = Command("SELECT count(*) FROM " + Quote(obj.Name) + where);
        foreach (var parameter in parameters) count.Parameters.AddWithValue(parameter.Key, parameter.Value);
        long total = Convert.ToInt64(count.ExecuteScalar());
        string[] selected = columns.Select(c => c.Name).Concat(identity.Names).ToArray();
        string order = sort != null && columns.Any(c => c.Name == sort) ? " ORDER BY " + Quote(sort) + (descending ? " DESC" : " ASC")
            : identity.Names.Length > 0 ? " ORDER BY " + string.Join(",", identity.Names.Select(Quote)) : "";
        using var cmd = Command("SELECT " + string.Join(",", selected.Select(Quote)) + " FROM " + Quote(obj.Name) + where + order + " LIMIT $limit OFFSET $offset");
        foreach (var parameter in parameters) cmd.Parameters.AddWithValue(parameter.Key, parameter.Value);
        cmd.Parameters.AddWithValue("$limit", PageSize); cmd.Parameters.AddWithValue("$offset", (long)Math.Max(0, page) * PageSize);
        using var r = cmd.ExecuteReader(); var rows = new List<BrowserRow>();
        while (r.Read())
        {
            cancel.ThrowIfCancellationRequested();
            var values = new object[columns.Length]; var keys = new object[identity.Names.Length];
            for (int i = 0; i < values.Length; i++) values[i] = r.GetValue(i);
            for (int i = 0; i < keys.Length; i++) keys[i] = r.GetValue(columns.Length + i);
            rows.Add(new(values, keys));
        }
        return new(columns, rows, total, identity.Names.Length > 0);
    }

    void WhereIdentity(SqliteCommand cmd, BrowserObject obj, BrowserRow row)
    {
        var identity = Identity(obj, Columns(obj.Name));
        if (identity.Names.Length == 0 || identity.Names.Length != row.Identity.Length)
            throw new InvalidOperationException("This table has no reliable row identity. Use SQL instead.");
        cmd.CommandText += " WHERE " + string.Join(" AND ", identity.Names.Select((n, i) => Quote(n) + " IS $key" + i));
        for (int i = 0; i < row.Identity.Length; i++) cmd.Parameters.AddWithValue("$key" + i, row.Identity[i]);
    }
    public void UpdateCell(BrowserObject obj, BrowserRow row, BrowserColumn column, BrowserValue value)
    {
        if (column.Hidden != 0) throw new InvalidOperationException("Generated columns cannot be edited.");
        object parsed = value.Parse();
        Atomic(() => {
            using var cmd = Command("UPDATE " + Quote(obj.Name) + " SET " + Quote(column.Name) + "=$value");
            cmd.Parameters.AddWithValue("$value", parsed); WhereIdentity(cmd, obj, row);
            if (cmd.ExecuteNonQuery() != 1) throw new InvalidOperationException("Row identity changed. Refresh and try again.");
        });
    }
    public BrowserRow UpdateCellAndRead(BrowserObject obj, BrowserRow row, BrowserColumn column, BrowserValue value)
    {
        if (column.Hidden != 0) throw new InvalidOperationException("Generated columns cannot be edited.");
        object parsed = value.Parse();
        var columns = Columns(obj.Name).Where(c => c.Hidden != 1).ToArray();
        var identity = Identity(obj, columns);
        string selection = string.Join(",", columns.Select(c => Quote(c.Name)).Concat(identity.Names.Select(Quote)));
        BrowserRow? updated = null;
        using var guard = Limit(default);
        Atomic(() => {
            using var update = Command("UPDATE " + Quote(obj.Name) + " SET " + Quote(column.Name) + "=$value");
            update.Parameters.AddWithValue("$value", parsed); WhereIdentity(update, obj, row);
            update.CommandText += " RETURNING " + selection;
            object[] keys = new object[identity.Names.Length];
            using (var reader = update.ExecuteReader()) {
                if (!reader.Read()) throw new InvalidOperationException("Row identity changed. Refresh and try again.");
                for (int i = 0; i < keys.Length; i++) keys[i] = reader.GetValue(columns.Length + i);
                if (reader.Read()) throw new InvalidOperationException("Row identity is not unique. Use SQL instead.");
            }
            // Read after AFTER triggers and SQLite affinity conversion. Preserve the new
            // identity when editing INTEGER PRIMARY KEY or WITHOUT ROWID key columns.
            using var read = Command("SELECT " + selection + " FROM " + Quote(obj.Name));
            WhereIdentity(read, obj, new BrowserRow([], keys));
            using var result = read.ExecuteReader();
            if (!result.Read()) throw new InvalidOperationException("A trigger moved or removed the edited row. The edit was rolled back; use SQL for this change.");
            var values = new object[columns.Length];
            for (int i = 0; i < values.Length; i++) values[i] = result.GetValue(i);
            for (int i = 0; i < keys.Length; i++) keys[i] = result.GetValue(values.Length + i);
            if (result.Read()) throw new InvalidOperationException("Row identity is not unique. Use SQL instead.");
            updated = new(values, keys);
        });
        return updated!;
    }

    public void Insert(BrowserObject obj, IReadOnlyDictionary<string, BrowserValue> values)
    {
        if (obj.Type != "table") throw new InvalidOperationException("Views are read-only in Browse Data.");
        var columns = Columns(obj.Name);
        if (values.Keys.Any(n => !columns.Any(c => c.Name == n && c.Hidden == 0))) throw new InvalidOperationException("Unknown or generated column.");
        var entries = values.Where(p => p.Value.Kind != "DEFAULT").ToArray();
        var parsed = entries.Select(p => p.Value.Parse()).ToArray();
        Atomic(() => {
            using var cmd = Command(entries.Length == 0 ? "INSERT INTO " + Quote(obj.Name) + " DEFAULT VALUES" :
                "INSERT INTO " + Quote(obj.Name) + " (" + string.Join(",", entries.Select(p => Quote(p.Key))) + ") VALUES (" +
                string.Join(",", entries.Select((_, i) => "$v" + i)) + ")");
            for (int i = 0; i < entries.Length; i++) cmd.Parameters.AddWithValue("$v" + i, parsed[i]);
            cmd.ExecuteNonQuery();
        });
    }
    public void Delete(BrowserObject obj, BrowserRow row) => Atomic(() => {
        using var cmd = Command("DELETE FROM " + Quote(obj.Name)); WhereIdentity(cmd, obj, row);
        if (cmd.ExecuteNonQuery() != 1) throw new InvalidOperationException("Row identity changed. Refresh and try again.");
    });

    void Atomic(Action action)
    {
        Execute("SAVEPOINT browser_operation");
        try { action(); Execute("RELEASE browser_operation"); _pending = true; }
        catch { RollbackOperation("browser_operation"); throw; }
    }
    // Keep the undo checkpoint separately. A SQLite interrupt can roll back an entire
    // write transaction, so never hold previous successful edits in that transaction.
    void SaveCheckpoint()
    {
        using var checkpoint = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = _checkpointPath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString());
        checkpoint.Open(); _db.BackupDatabase(checkpoint);
    }
    public void WriteChanges() { SaveCheckpoint(); HasChanges |= _pending; _pending = false; }
    public void RevertChanges()
    {
        using var checkpoint = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = _checkpointPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        checkpoint.Open(); checkpoint.BackupDatabase(_db); _pending = false;
    }
    void RollbackOperation(string savepoint)
    {
        // Interrupted writes may already have rolled back their operation transaction.
        if (raw.sqlite3_get_autocommit(_db.Handle) != 0) return;
        Execute("ROLLBACK TO " + savepoint); Execute("RELEASE " + savepoint);
    }

    // Native authorizer is the enforcement boundary, not a SQL keyword/regex filter.
    sealed class AuthorizationState { public string? Denied { get; set; } }
    static int Authorize(object state, int action, string a, string b, string database, string trigger)
    {
        int Deny() { if (state is AuthorizationState s) s.Denied=$"SQLite action {action}: {a} {b} ({database})"; return raw.SQLITE_DENY; }
        if (action is raw.SQLITE_ATTACH or raw.SQLITE_DETACH or raw.SQLITE_TRANSACTION or raw.SQLITE_SAVEPOINT or
            raw.SQLITE_CREATE_VTABLE or raw.SQLITE_DROP_VTABLE) return Deny();
        if (action == raw.SQLITE_FUNCTION && new[] { "load_extension", "readfile", "writefile", "edit" }.Contains(b, StringComparer.OrdinalIgnoreCase))
            return Deny();
        if (action == raw.SQLITE_PRAGMA)
        {
            string[] readable = ["table_info", "table_xinfo", "table_list", "foreign_key_list", "foreign_key_check", "index_list", "index_info", "index_xinfo", "quick_check", "integrity_check", "database_list", "compile_options"];
            if (!readable.Contains(a, StringComparer.OrdinalIgnoreCase) &&
                !a.Equals("user_version", StringComparison.OrdinalIgnoreCase) && !a.Equals("application_id", StringComparison.OrdinalIgnoreCase)) return Deny();
        }
        // ALTER TABLE rewrites SQLite's own temporary schema metadata even for
        // a main-database table. This does not grant TEMP table creation/file access.
        bool internalSchemaRewrite = action == raw.SQLITE_UPDATE && a is "sqlite_temp_master" or "sqlite_master";
        if (database == "temp" && !internalSchemaRewrite && action is not (raw.SQLITE_SELECT or raw.SQLITE_READ or raw.SQLITE_FUNCTION)) return Deny();
        return raw.SQLITE_OK;
    }
    IDisposable Limit(CancellationToken cancel)
    {
        var timer = Stopwatch.StartNew();
        raw.sqlite3_progress_handler(_db.Handle, 1000, _ => cancel.IsCancellationRequested || timer.Elapsed > TimeSpan.FromSeconds(15) ? 1 : 0, null);
        return new CallbackScope(() => raw.sqlite3_progress_handler(_db.Handle, 0, null!, null));
    }
    sealed class CallbackScope(Action release) : IDisposable { public void Dispose() => release(); }

    public (DataTable Results, string Message) RunSql(string sql, CancellationToken cancel = default)
    {
        if (string.IsNullOrWhiteSpace(sql)) throw new InvalidOperationException("Enter SQL first.");
        long before = Scalar("SELECT total_changes()"); long schemaBefore = Scalar("PRAGMA schema_version");
        long userBefore = Scalar("PRAGMA user_version"), appBefore = Scalar("PRAGMA application_id");
        var results = new DataTable(); bool truncated = false; var scriptTimer = Stopwatch.StartNew();
        AtomicSql();
        return (results, $"Completed. {Scalar("SELECT total_changes()") - before:N0} row changes; last result shows {results.Rows.Count:N0} rows" + (truncated ? " (limited to 1,000)." : "."));

        void AtomicSql()
        {
            var authorization = new AuthorizationState();
            Execute("SAVEPOINT browser_sql");
            try
            {
                using (Limit(cancel))
                {
                    if (raw.sqlite3_set_authorizer(_db.Handle, (strdelegate_authorizer)Authorize, authorization) != raw.SQLITE_OK)
                        throw new InvalidOperationException("Could not enable the SQL safety boundary.");
                    try
                    {
                        using var cmd = Command(sql); using var r = cmd.ExecuteReader();
                        do
                        {
                            cancel.ThrowIfCancellationRequested();
                            if (scriptTimer.Elapsed > TimeSpan.FromSeconds(15)) throw new TimeoutException("SQL execution exceeded 15 seconds. The script was rolled back.");
                            if (r.FieldCount == 0) continue;
                            results = new DataTable(); truncated = false;
                            for (int i = 0; i < r.FieldCount; i++) results.Columns.Add("c" + i, typeof(string)).Caption = r.GetName(i);
                            while (r.Read())
                            {
                                cancel.ThrowIfCancellationRequested();
                                if (results.Rows.Count == SqlResultLimit) { truncated = true; break; }
                                results.Rows.Add(Enumerable.Range(0, r.FieldCount).Select(i => (object)Display(r.GetValue(i))).ToArray());
                            }
                        } while (r.NextResult());
                    }
                    finally { raw.sqlite3_set_authorizer(_db.Handle, (strdelegate_authorizer)null!, null); }
                }
                cancel.ThrowIfCancellationRequested();
                bool changed = Scalar("SELECT total_changes()") != before || Scalar("PRAGMA schema_version") != schemaBefore ||
                    Scalar("PRAGMA user_version") != userBefore || Scalar("PRAGMA application_id") != appBefore;
                Execute("RELEASE browser_sql"); _pending |= changed;
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode==23) { RollbackOperation("browser_sql"); throw new InvalidOperationException("SQL was blocked by the working-copy safety guard. " + authorization.Denied,ex); }
            catch { RollbackOperation("browser_sql"); throw; }
        }
    }
    public static string Display(object value) => value switch
    {
        DBNull => "NULL",
        byte[] b => "BLOB (" + b.Length + " bytes): " + Convert.ToHexString(b.AsSpan(0, Math.Min(b.Length, 64))) + (b.Length > 64 ? "…" : ""),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""
    };

    public IReadOnlyList<KeyValuePair<string,string>> Properties()
    {
        string[] names = ["user_version", "application_id", "page_size", "page_count", "freelist_count", "encoding", "journal_mode", "foreign_keys", "schema_version"];
        return names.Select(name => { using var cmd = Command("PRAGMA " + name); return new KeyValuePair<string,string>(name, Convert.ToString(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) ?? ""); }).ToArray();
    }

    public void ExportCsv(BrowserObject obj, string output, CancellationToken cancel = default)
    {
        output = Path.GetFullPath(output);
        if (File.Exists(output) || output.Equals(SourcePath, StringComparison.OrdinalIgnoreCase)) throw new IOException("Choose a new CSV filename; existing files are never overwritten.");
        string partial = output + "." + Guid.NewGuid().ToString("N") + ".partial";
        try {
            var columns = Columns(obj.Name).Where(c=>c.Hidden==0).ToArray();
            if (columns.Length==0) throw new InvalidOperationException("No exportable columns.");
            using (Limit(cancel)) using (var cmd = Command("SELECT " + string.Join(",",columns.Select(c=>Quote(c.Name))) + " FROM " + Quote(obj.Name))) using (var reader = cmd.ExecuteReader())
            using (var writer = new StreamWriter(new FileStream(partial, FileMode.CreateNew, FileAccess.Write), new System.Text.UTF8Encoding(true))) {
                static string Csv(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
                writer.WriteLine(string.Join(",", Enumerable.Range(0,reader.FieldCount).Select(i => Csv(reader.GetName(i)))));
                while (reader.Read()) { cancel.ThrowIfCancellationRequested(); writer.WriteLine(string.Join(",", Enumerable.Range(0,reader.FieldCount).Select(i => reader.IsDBNull(i) ? "" : Csv(BrowserValue.From(reader.GetValue(i)).Text)))); }
            }
            cancel.ThrowIfCancellationRequested(); File.Move(partial, output);
        } finally { DeleteOwned(partial); }
    }

    public void Export(string output, string? template = null)
    {
        output = Path.GetFullPath(output);
        if (output.Equals(SourcePath, StringComparison.OrdinalIgnoreCase) || File.Exists(output))
            throw new IOException("Choose a new filename. Input files and existing outputs are never overwritten.");
        bool slt = GameDbSqliteBridge.IsSlt(output);
        if (slt)
        {
            template ??= GameDbSqliteBridge.IsSlt(SourcePath) ? SourcePath : null;
            if (template == null) throw new InvalidOperationException("Choose an original encrypted GameDB SLT template to export SQLite as SLT.");
            if (_sourceHash != null && Path.GetFullPath(template).Equals(SourcePath, StringComparison.OrdinalIgnoreCase))
                GameDbSqliteBridge.VerifySourceHash(SourcePath, _sourceHash);
        }
        WriteChanges();
        string partial = output + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            using (var destination = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = partial, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString()))
            {
                destination.Open(); _db.BackupDatabase(destination);
                using var check = destination.CreateCommand(); check.CommandText = "PRAGMA quick_check";
                if (!string.Equals(Convert.ToString(check.ExecuteScalar()), "ok", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Export failed SQLite quick_check.");
            }
            if (slt) GameDbSqliteBridge.EncryptSnapshot(partial, template!, output);
            else File.Move(partial, output);
            HasChanges = false;
        }
        finally { DeleteOwned(partial); }
    }
    static void DeleteOwned(string path)
    {
        // Called only with a unique path created and owned by this instance.
        foreach (string owned in new[] { path, path + "-wal", path + "-shm", path + "-journal" }) try { File.Delete(owned); } catch { }
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        _db.Dispose(); DeleteOwned(_workPath); DeleteOwned(_checkpointPath);
    }
}
