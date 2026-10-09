using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace FH6LocalCryptoTool.DbBrowser;

public sealed partial class BrowserDatabase
{
    public static BrowserDatabase New()
    {
        string path = Path.Combine(FH6LocalCryptoTool.TempWorkspace.Root, "fh6_db_browser_" + Guid.NewGuid().ToString("N") + ".sqlite");
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString());
        try { db.Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "PRAGMA user_version=0"; cmd.ExecuteNonQuery(); return new BrowserDatabase(path + ".unsaved", path, null, db); }
        catch { db.Dispose(); DeleteOwned(path); DeleteOwned(path + ".checkpoint"); throw; }
    }

    public static string SqlLiteral(object value) => value switch {
        DBNull => "NULL",
        byte[] blob => "X'" + Convert.ToHexString(blob) + "'",
        long number => number.ToString(CultureInfo.InvariantCulture),
        double number => double.IsPositiveInfinity(number) ? "9e999" : double.IsNegativeInfinity(number) ? "-9e999" : number.ToString("R", CultureInfo.InvariantCulture),
        string text when text.Contains('\0') => "CAST(X'" + Convert.ToHexString(Encoding.UTF8.GetBytes(text)) + "' AS TEXT)",
        _ => "'" + (Convert.ToString(value, CultureInfo.InvariantCulture) ?? "").Replace("'", "''") + "'"
    };

    public void ExportSql(string output, CancellationToken cancel = default)
    {
        output = Path.GetFullPath(output);
        if (File.Exists(output) || output.Equals(SourcePath, StringComparison.OrdinalIgnoreCase)) throw new IOException("Choose a new SQL filename.");
        var objects = Objects(); var tables = objects.Where(o => o.Type == "table").ToArray();
        if (tables.Any(t => t.Sql.Contains("CREATE VIRTUAL", StringComparison.OrdinalIgnoreCase))) throw new NotSupportedException("SQL dumps of virtual tables are not supported; export SQLite instead.");
        string partial = output + "." + Guid.NewGuid().ToString("N") + ".partial";
        try {
            using (Limit(cancel)) using (var writer = new StreamWriter(new FileStream(partial, FileMode.CreateNew), new UTF8Encoding(false))) {
                writer.WriteLine("-- DB Browser working-copy dump. Import as one atomic operation in the tool.");
                foreach (var table in tables) writer.WriteLine(table.Sql.TrimEnd(';') + ";");
                foreach (var table in tables) WriteRows(table.Name);
                // sqlite_sequence must retain AUTOINCREMENT counters even when the highest rows were deleted.
                if (Scalar("SELECT count(*) FROM sqlite_schema WHERE name='sqlite_sequence'") != 0) {
                    writer.WriteLine("DELETE FROM sqlite_sequence;"); WriteRows("sqlite_sequence");
                }
                foreach (var obj in objects.Where(o => o.Type != "table" && o.Sql.Length > 0)) writer.WriteLine(obj.Sql.TrimEnd(';') + ";");
                writer.WriteLine("PRAGMA user_version=" + Scalar("PRAGMA user_version") + ";");
                writer.WriteLine("PRAGMA application_id=" + Scalar("PRAGMA application_id") + ";");

                void WriteRows(string table)
                {
                    var columns = Columns(table).Where(c => c.Hidden == 0).ToArray(); if (columns.Length == 0) return;
                    string names = string.Join(",",columns.Select(c => Quote(c.Name)));
                    using var cmd = Command("SELECT " + names + " FROM " + Quote(table)); using var r = cmd.ExecuteReader();
                    while (r.Read()) { cancel.ThrowIfCancellationRequested(); writer.WriteLine("INSERT INTO " + Quote(table) + " (" + names + ") VALUES (" + string.Join(",",Enumerable.Range(0,r.FieldCount).Select(i => SqlLiteral(r.GetValue(i)))) + ");"); }
                }
            }
            cancel.ThrowIfCancellationRequested(); File.Move(partial, output);
        } finally { DeleteOwned(partial); }
    }

    /// <summary>SQLite's own completeness scanner handles quoted semicolons and trigger bodies.</summary>
    public static string NormalizeSqlImport(string script, CancellationToken cancel = default)
    {
        if (script.Length > 64 * 1024 * 1024) throw new InvalidDataException("SQL imports are limited to 64 MB.");
        var result = new StringBuilder(); int start = 0; char quote='\0'; bool lineComment=false,blockComment=false;
        for (int i = 0; i < script.Length; i++) {
            if ((i & 4095)==0) cancel.ThrowIfCancellationRequested();
            char c=script[i], next=i+1<script.Length?script[i+1]:'\0';
            if(lineComment){if(c is '\r' or '\n')lineComment=false;continue;}
            if(blockComment){if(c=='*'&&next=='/'){blockComment=false;i++;}continue;}
            if(quote!='\0'){if(c==quote){if(next==quote&&quote!=']')i++;else quote='\0';}continue;}
            if(c=='-'&&next=='-'){lineComment=true;i++;continue;}
            if(c=='/'&&next=='*'){blockComment=true;i++;continue;}
            if(c is '\'' or '"' or '`' or '['){quote=c=='['?']':c;continue;}
            if(c!=';')continue;
            string candidate = script[start..(i+1)];
            if (raw.sqlite3_complete(candidate) == 0) continue;
            Add(candidate); start = i + 1;
        }
        Add(script[start..]); return result.ToString();

        void Add(string statement)
        {
            string stripped = Regex.Replace(statement, @"\A(?:\s|--[^\r\n]*(?:\r?\n|$)|/\*[\s\S]*?\*/)*", "");
            // External dumps often include wrappers. Our runner supplies the atomic transaction.
            if (Regex.IsMatch(stripped, @"\A(?:BEGIN(?:\s+(?:DEFERRED|IMMEDIATE|EXCLUSIVE))?(?:\s+TRANSACTION)?|COMMIT(?:\s+TRANSACTION)?|END(?:\s+TRANSACTION)?|PRAGMA\s+foreign_keys\s*=\s*(?:ON|OFF|0|1))\s*;?\s*\z", RegexOptions.IgnoreCase)) return;
            result.AppendLine(statement);
        }
    }

    public int ImportCsv(BrowserObject obj, string input, CancellationToken cancel = default)
    {
        if (obj.Type != "table" || obj.Sql.Contains("CREATE VIRTUAL", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Choose a regular table to import CSV records.");
        if (new FileInfo(input).Length > 64 * 1024 * 1024) throw new InvalidDataException("CSV imports are limited to 64 MB.");
        var rows = ReadCsv(File.ReadAllText(input), cancel);
        if (rows.Count == 0) throw new InvalidDataException("CSV has no header row.");
        var columns = Columns(obj.Name); var headers = rows[0].Select(f => f.Text).ToArray();
        if (headers.Distinct(StringComparer.OrdinalIgnoreCase).Count() != headers.Length) throw new InvalidDataException("CSV headers must be unique.");
        var mapped = headers.Select(name => columns.FirstOrDefault(c => c.Name.Equals(name,StringComparison.OrdinalIgnoreCase) && c.Hidden == 0)
            ?? throw new InvalidDataException("Unknown or generated CSV column: " + name)).ToArray();
        int count = 0; bool pendingBefore = _pending;
        try {
            Atomic(() => {
                using var limit = Limit(cancel);
                using var cmd = Command("INSERT INTO " + Quote(obj.Name) + " (" + string.Join(",",mapped.Select(c=>Quote(c.Name))) + ") VALUES (" + string.Join(",",mapped.Select((_,i)=>"$v"+i)) + ")");
                for (int i = 0; i < mapped.Length; i++) cmd.Parameters.AddWithValue("$v"+i, DBNull.Value);
                for (int row = 1; row < rows.Count; row++) {
                    cancel.ThrowIfCancellationRequested();
                    if (rows[row].Length != headers.Length) throw new InvalidDataException("CSV row " + (row+1) + " has a different number of fields.");
                    for (int i = 0; i < mapped.Length; i++) {
                        var field = rows[row][i]; string type = mapped[i].Type.ToUpperInvariant(); object value;
                        if (!field.Quoted && field.Text.Length == 0) value = DBNull.Value;
                        else if (type.Contains("INT") && field.Text.Length > 0) value = new BrowserValue("INTEGER",field.Text).Parse();
                        else if (new[] {"REAL","FLOA","DOUB"}.Any(type.Contains) && field.Text.Length > 0) value = new BrowserValue("REAL",field.Text).Parse();
                        else if (type == "BLOB") value = new BrowserValue("BLOB",field.Text).Parse();
                        else value = field.Text;
                        cmd.Parameters[i].Value = value;
                    }
                    cmd.ExecuteNonQuery(); count++;
                }
            });
        } catch { _pending = pendingBefore; throw; }
        return count;
    }

    sealed record CsvField(string Text, bool Quoted);
    static List<CsvField[]> ReadCsv(string text, CancellationToken cancel)
    {
        var rows = new List<CsvField[]>(); var fields = new List<CsvField>(); var value = new StringBuilder();
        bool quoted = false, inQuote = false, closedQuote = false, started = false;
        for (int i = 0; i < text.Length; i++) {
            if ((i & 4095)==0) cancel.ThrowIfCancellationRequested();
            char c = text[i]; started = true;
            if (inQuote) {
                if (c == '"') { if (i+1 < text.Length && text[i+1] == '"') { value.Append('"'); i++; } else { inQuote = false; closedQuote = true; } }
                else value.Append(c);
            } else if (c == ',' || c is '\r' or '\n') {
                fields.Add(new(value.ToString(),quoted)); value.Clear(); quoted = closedQuote = false;
                if (fields.Count > 2048) throw new InvalidDataException("CSV imports support up to 2,048 columns.");
                if (c != ',') { if (c == '\r' && i+1 < text.Length && text[i+1] == '\n') i++; rows.Add(fields.ToArray()); fields.Clear(); started = false; }
                if (rows.Count > 100001) throw new InvalidDataException("CSV imports are limited to 100,000 records.");
            } else if (c == '"' && value.Length == 0 && !closedQuote) { quoted = inQuote = true; }
            else if (closedQuote || c == '"') throw new InvalidDataException("Malformed CSV quoting.");
            else value.Append(c);
        }
        if (inQuote) throw new InvalidDataException("Unclosed CSV quoted field.");
        if (started || fields.Count > 0) { fields.Add(new(value.ToString(),quoted)); rows.Add(fields.ToArray()); }
        return rows;
    }
}
