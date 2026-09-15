#nullable disable
using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Microsoft.Data.Sqlite;
using Microsoft.Win32;
using FH6LocalCryptoTool;

namespace FH6ProfileEditor;

public partial class ProfileEditorView : UserControl
{
    // The four decrypted section markers begin with "profile" (see ProfileContainer).
    static readonly byte[] ProfileMarkerLE = { 0xB6, 0xF2, 0x8B, 0x4A };
    const int RowLimit = 500;

    byte[] _originalBytes;          // the file the user loaded (re-encrypt template)
    bool _outerCryptoApplied;       // true = encrypted C_ProfileData; false = already-decrypted stream
    ProfileContainer _container;    // parsed sections (SQLite in section 3)
    string _sqlitePath;             // temp working copy of the embedded database
    SqliteConnection _conn;
    string _loadedName;
    string _currentTable;
    readonly List<string> _tables = new();

    // Typed-property ("profile" section) editing surface.
    readonly List<(string Path, PropertyTree.PropertyNode Node)> _allProps = new();
    DataTable _propTable;

    static readonly Brush BrPink = Freeze(Color.FromRgb(0xE8, 0x17, 0x5D));
    static readonly Brush BrDim  = Freeze(Color.FromRgb(0x9C, 0x9C, 0xA2));
    static readonly Brush BrOk   = Freeze(Color.FromRgb(0x63, 0xC8, 0x7A));
    static readonly Brush BrErr  = Freeze(Color.FromRgb(0xF0, 0x60, 0x60));
    static readonly Brush BrXmlName = Freeze(Color.FromRgb(0xFF, 0x4A, 0x84));
    static readonly Brush BrXmlAttr = Freeze(Color.FromRgb(0x72, 0xC7, 0xE8));
    static readonly Brush BrXmlValue = Freeze(Color.FromRgb(0xE8, 0xB0, 0x48));
    static readonly Brush BrXmlComment = Freeze(Color.FromRgb(0x63, 0xC8, 0x7A));
    static Brush Freeze(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    public ProfileEditorView()
    {
        InitializeComponent();
        LogBox.Document = new FlowDocument { PagePadding = new Thickness(0) };
        RowGrid.AutoGeneratingColumn += RowGrid_AutoGenColumn;
        PropGrid.AutoGeneratingColumn += PropGrid_AutoGenColumn;
        StringGrid.AutoGeneratingColumn += StringGrid_AutoGenColumn;
        CareerGrid.AutoGeneratingColumn += CareerGrid_AutoGenColumn;
        Log("Ready. Load a C_ProfileData save to begin.", "info");
    }

    // ============================================================ log
    void Log(string message, string cls = "info")
    {
        var (prefix, brush) = cls switch
        {
            "ok"   => ("✓ ", BrOk),
            "err"  => ("✗ ", BrErr),
            _      => ("· ", BrDim),
        };
        var p = new Paragraph { Margin = new Thickness(0) };
        p.Inlines.Add(new Run(DateTime.Now.ToString("HH:mm:ss") + "  ") { Foreground = BrPink });
        p.Inlines.Add(new Run(prefix + message) { Foreground = brush });
        LogBox.Document.Blocks.Add(p);
        LogBox.ScrollToEnd();
    }

    // ============================================================ load
    void Load_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Select a Forza Horizon 6 C_ProfileData save",
            Filter = "ProfileData (C_ProfileData*)|C_ProfileData*|All files|*.*",
        };
        if (dlg.ShowDialog() != true) return;
        try { LoadProfile(dlg.FileName); }
        catch (Exception ex) { Fail("load", ex); }
    }

    void LoadProfile(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        byte[] stream;
        if (bytes.Length >= 4 && bytes.AsSpan(0, 4).SequenceEqual(ProfileMarkerLE))
        {
            stream = bytes;                    // already-decrypted, uncompressed section stream
            _outerCryptoApplied = false;
        }
        else
        {
            stream = ProfileData.Decrypt(bytes); // encrypted C_ProfileData -> inflated section stream
            _outerCryptoApplied = true;
        }

        var container = ProfileContainer.Parse(stream);

        CloseConnection();
        string dir = Path.Combine(Path.GetTempPath(), "FH6LocalCryptoTool");
        Directory.CreateDirectory(dir);
        _sqlitePath = Path.Combine(dir, $"profile.{Environment.ProcessId}.{Guid.NewGuid():N}.sqlite");
        File.WriteAllBytes(_sqlitePath, container.Database);
        OpenConnection();

        string integrity;
        using (var cmd = _conn.CreateCommand()) { cmd.CommandText = "PRAGMA integrity_check"; integrity = Convert.ToString(cmd.ExecuteScalar()); }
        if (!string.Equals(integrity, "ok", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"the embedded profile database failed its integrity check ({integrity}).");

        _originalBytes = bytes;
        _container = container;
        _loadedName = Path.GetFileName(path);

        LoadTableList();
        SummaryText.Text =
            $"{_loadedName}   ·   {_tables.Count} data tables   ·   database {container.Database.Length:n0} bytes   ·   integrity {integrity}   ·   " +
            (_outerCryptoApplied ? "encrypted save (re-encrypted automatically when you Save)" : "already-decrypted stream");
        StatusLine.Text = $"{_loadedName} loaded — {_tables.Count} tables";
        SaveBtn.IsEnabled = true;
        Log($"loaded {_loadedName}: {_tables.Count} tables, database {container.Database.Length:n0} bytes, integrity {integrity}", "ok");

        BuildProperties();
        BuildSaveState();
        BuildCareer();
        BuildOverview();
        ShowOverviewView();   // land on the friendly Overview after a load
    }

    // ============================================================ sqlite plumbing
    void OpenConnection()
    {
        _conn = new SqliteConnection($"Data Source={_sqlitePath};Pooling=False");
        _conn.Open();
    }

    void CloseConnection()
    {
        try { _conn?.Close(); _conn?.Dispose(); } catch { }
        _conn = null;
        SqliteConnection.ClearAllPools();
    }

    void LoadTableList()
    {
        _tables.Clear();
        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name";
            using var r = cmd.ExecuteReader();
            while (r.Read()) _tables.Add(r.GetString(0));
        }
        RenderTables();
    }

    void RenderTables()
    {
        string term = (TableSearch.Text ?? "").Trim().ToLowerInvariant();
        TableList.Items.Clear();
        foreach (var name in _tables)
        {
            if (term.Length > 0 && !name.ToLowerInvariant().Contains(term)) continue;
            long count = 0;
            try { using var cmd = _conn.CreateCommand(); cmd.CommandText = $"SELECT COUNT(*) FROM {Quote(name)}"; count = Convert.ToInt64(cmd.ExecuteScalar()); } catch { }
            TableList.Items.Add(new ListBoxItem { Content = $"{name}   ({count})", Tag = name, Foreground = (Brush)FindResource("Ink") });
        }
    }

    void TableSearch_Changed(object sender, TextChangedEventArgs e)
    {
        if (_conn != null) RenderTables();
    }

    void TableList_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (TableList.SelectedItem is ListBoxItem item && item.Tag is string name)
        {
            try { LoadTable(name); } catch (Exception ex) { Fail("open table", ex); }
        }
    }

    void LoadTable(string table)
    {
        _currentTable = table;
        var dt = new DataTable();
        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = $"SELECT rowid AS __rowid__, * FROM {Quote(table)} LIMIT {RowLimit}";
            using var r = cmd.ExecuteReader();
            for (int i = 0; i < r.FieldCount; i++) dt.Columns.Add(r.GetName(i), typeof(string));
            while (r.Read())
            {
                var row = dt.NewRow();
                for (int i = 0; i < r.FieldCount; i++) row[i] = DisplayValue(r.IsDBNull(i) ? null : r.GetValue(i));
                dt.Rows.Add(row);
            }
        }
        long total; using (var c = _conn.CreateCommand()) { c.CommandText = $"SELECT COUNT(*) FROM {Quote(table)}"; total = Convert.ToInt64(c.ExecuteScalar()); }
        RowGrid.ItemsSource = dt.DefaultView;
        GridTitle.Text = $"{table.ToUpperInvariant()}   ·   showing {dt.Rows.Count} of {total:n0}";
    }

    void RowGrid_AutoGenColumn(object sender, DataGridAutoGeneratingColumnEventArgs e)
    {
        if (e.PropertyName == "__rowid__") e.Cancel = true;   // keep in the data, hide from view
    }

    void RowGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction != DataGridEditAction.Commit) return;
        if (_conn == null || _currentTable == null) return;
        if (e.Row.Item is not DataRowView view) return;
        string column = e.Column?.Header?.ToString();
        if (string.IsNullOrEmpty(column) || column == "__rowid__") return;
        string text = (e.EditingElement as TextBox)?.Text ?? "";
        if (text.StartsWith("<blob", StringComparison.OrdinalIgnoreCase))
        {
            Log($"{_currentTable}.{column}: blob cells can't be edited as text (skipped)", "err");
            return;
        }
        object rowidObj = view["__rowid__"];
        try
        {
            long rowid = Convert.ToInt64(rowidObj, CultureInfo.InvariantCulture);
            object bound = ParseCell(text);
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = $"UPDATE {Quote(_currentTable)} SET {Quote(column)}=$v WHERE rowid=$rid";
            cmd.Parameters.AddWithValue("$v", bound ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$rid", rowid);
            int n = cmd.ExecuteNonQuery();
            if (n > 0) Log($"{_currentTable}.{column} → {text}  (rowid {rowid})", "ok");
            else Log($"{_currentTable}.{column}: no row updated (rowid {rowid})", "err");
        }
        catch (Exception ex)
        {
            Log($"edit rejected: {ex.Message}", "err");
        }
    }

    // ============================================================ SQL runner
    void RunSql_Click(object sender, RoutedEventArgs e)
    {
        if (_conn == null) { Log("load a profile first", "err"); return; }
        string sql = (SqlBox.Text ?? "").Trim();
        if (sql.Length == 0) { Log("enter a SQL statement", "err"); return; }
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = sql;
            using var r = cmd.ExecuteReader();
            if (r.FieldCount > 0)
            {
                int rows = 0; var sb = new StringBuilder();
                var cols = Enumerable.Range(0, r.FieldCount).Select(r.GetName).ToArray();
                sb.Append(string.Join(" | ", cols));
                while (r.Read() && rows < 50)
                {
                    sb.Append(Environment.NewLine);
                    sb.Append(string.Join(" | ", Enumerable.Range(0, r.FieldCount).Select(i => DisplayValue(r.IsDBNull(i) ? null : r.GetValue(i)))));
                    rows++;
                }
                Log($"query returned {rows}{(rows >= 50 ? "+" : "")} row(s):", "ok");
                foreach (var line in sb.ToString().Split('\n')) Log("   " + line.TrimEnd('\r'), "info");
            }
            else
            {
                Log($"statement affected {r.RecordsAffected} row(s)", "ok");
            }
        }
        catch (Exception ex) { Log($"SQL error: {ex.Message}", "err"); return; }

        // refresh the visible table + counts after a possible modification
        RenderTables();
        if (_currentTable != null) { try { LoadTable(_currentTable); } catch { } }
    }

    // ============================================================ view toggle
    void ViewOverview_Click(object sender, RoutedEventArgs e) => ShowOverviewView();
    void ViewDb_Click(object sender, RoutedEventArgs e) => ShowDatabaseView();
    void ViewProps_Click(object sender, RoutedEventArgs e) => ShowPropertiesView();
    void ViewSave_Click(object sender, RoutedEventArgs e) => ShowSaveStateView();
    void ViewCareer_Click(object sender, RoutedEventArgs e) => ShowCareerView();

    void SetActiveButton(Button active)
    {
        var pink = (Style)FindResource("PinkButton");
        var ghost = (Style)FindResource("GhostButton");
        ViewOverviewBtn.Style = ReferenceEquals(active, ViewOverviewBtn) ? pink : ghost;
        ViewDbBtn.Style = ReferenceEquals(active, ViewDbBtn) ? pink : ghost;
        ViewPropBtn.Style = ReferenceEquals(active, ViewPropBtn) ? pink : ghost;
        ViewSaveBtn.Style = ReferenceEquals(active, ViewSaveBtn) ? pink : ghost;
        ViewCareerBtn.Style = ReferenceEquals(active, ViewCareerBtn) ? pink : ghost;
    }

    void ShowOnly(UIElement view)
    {
        OverviewView.Visibility = ReferenceEquals(view, OverviewView) ? Visibility.Visible : Visibility.Collapsed;
        DbBody.Visibility     = ReferenceEquals(view, DbBody)     ? Visibility.Visible : Visibility.Collapsed;
        PropView.Visibility   = ReferenceEquals(view, PropView)   ? Visibility.Visible : Visibility.Collapsed;
        SaveView.Visibility   = ReferenceEquals(view, SaveView)   ? Visibility.Visible : Visibility.Collapsed;
        CareerView.Visibility = ReferenceEquals(view, CareerView) ? Visibility.Visible : Visibility.Collapsed;
        SqlCard.Visibility    = ReferenceEquals(view, DbBody)     ? Visibility.Visible : Visibility.Collapsed;
    }

    void ShowOverviewView()
    {
        ShowOnly(OverviewView);
        SetActiveButton(ViewOverviewBtn);
        ViewHelp.Text = "Overview — the quick way in. A snapshot of your save, plus the values people most often change. "
                      + "For everything else, use the other buttons.";
    }

    void ShowDatabaseView()
    {
        ShowOnly(DbBody);
        SetActiveButton(ViewDbBtn);
        ViewHelp.Text = "Cars & Data — your cars, garage, barn finds, purchased parts and photos, stored as tables. "
                      + "Pick a table on the left, then double-click any cell to change it.";
    }

    void ShowPropertiesView()
    {
        ShowOnly(PropView);
        SetActiveButton(ViewPropBtn);
        ViewHelp.Text = "Profile Values — individual settings and counters saved in your profile. "
                      + "Double-click a value to edit it; the 'Kind' column tells you what sort of value it expects.";
    }

    void ShowSaveStateView()
    {
        ShowOnly(SaveView);
        SetActiveButton(ViewSaveBtn);
        ViewHelp.Text = "Save State — structured game data stored as an internal XML. "
                      + "You can edit the text values on the left; the panel on the right shows the full structure for reference.";
    }

    void ShowCareerView()
    {
        ShowOnly(CareerView);
        SetActiveButton(ViewCareerBtn);
        ViewHelp.Text = "Career — low-level career records. These are shown for reference and aren't safe to hand-edit yet, "
                      + "but you can change the Account ID (XUID) this save belongs to.";
    }

    // ============================================================ typed properties (section 0)
    void BuildProperties()
    {
        _allProps.Clear();
        _propTable = null;
        PropGrid.ItemsSource = null;
        if (PropSearch != null) PropSearch.Text = "";

        var tree = _container?.Properties;
        if (tree == null)
        {
            PropTitle.Text = "PROFILE PROPERTIES   ·   read-only (section 0 not portable in this save)";
            Log("typed properties: section 0 couldn't be safely parsed for editing (round-trip guard) — SQLite editing is unaffected", "info");
            return;
        }

        foreach (var pair in tree.Walk()) _allProps.Add(pair);
        int editable = _allProps.Count(p => p.Node.IsEditable);
        PropTitle.Text = $"PROFILE PROPERTIES   ·   {_allProps.Count:n0} nodes, {editable:n0} editable";
        Log($"typed properties: {_allProps.Count:n0} nodes ({editable:n0} editable)", "ok");
        RenderProperties();
    }

    void RenderProperties()
    {
        if (_allProps.Count == 0) { PropGrid.ItemsSource = null; return; }
        string term = (PropSearch.Text ?? "").Trim().ToLowerInvariant();

        var dt = new DataTable();
        dt.Columns.Add("__idx__", typeof(string));
        dt.Columns.Add("__editable__", typeof(string));
        dt.Columns.Add("Property", typeof(string));
        dt.Columns.Add("Kind", typeof(string));
        dt.Columns.Add("Value", typeof(string));

        for (int i = 0; i < _allProps.Count; i++)
        {
            var (path, node) = _allProps[i];
            if (term.Length > 0 && !path.ToLowerInvariant().Contains(term)) continue;
            var row = dt.NewRow();
            row["__idx__"] = i.ToString(CultureInfo.InvariantCulture);
            row["__editable__"] = node.IsEditable ? "1" : "0";
            row["Property"] = FriendlyPath(path);
            row["Kind"] = FriendlyType(node);
            row["Value"] = node.DisplayValue();
            dt.Rows.Add(row);
        }
        _propTable = dt;
        PropGrid.ItemsSource = dt.DefaultView;
    }

    void PropSearch_Changed(object sender, TextChangedEventArgs e)
    {
        if (_allProps.Count > 0) RenderProperties();
    }

    void PropGrid_AutoGenColumn(object sender, DataGridAutoGeneratingColumnEventArgs e)
    {
        switch (e.PropertyName)
        {
            case "__idx__":
            case "__editable__":
                e.Cancel = true; break;
            case "Property":
                e.Column.IsReadOnly = true;
                e.Column.Width = new DataGridLength(2, DataGridLengthUnitType.Star); break;
            case "Kind":
                e.Column.IsReadOnly = true;
                e.Column.Width = new DataGridLength(1, DataGridLengthUnitType.Star); break;
            case "Value":
                e.Column.Width = new DataGridLength(1.4, DataGridLengthUnitType.Star); break;
        }
    }

    // Plain-language type names, so a save editor doesn't have to know FH6's CVariant names.
    static string FriendlyType(PropertyTree.PropertyNode node) => node.Spec.Name switch
    {
        "Bool" => "on / off",
        "UInt8" or "UInt16" or "UInt32" or "UInt64" or
        "Int8" or "Int16" or "Int32" or "Int64" or "U32_Obfuscated" => "whole number",
        "Float32" or "Float64" => "decimal number",
        "StringNarrow" or "StringWide" => "text",
        "Matrix" => "matrix (read-only)",
        "Vector" => "vector (read-only)",
        "PropertyBag" or "DatabasePropertyBag" => "group",
        _ => node.TypeName,
    };

    // "/Career/Credits" -> "Career › Credits" (search still uses the raw path).
    static string FriendlyPath(string path) => path.TrimStart('/').Replace("/", "  ›  ");

    void PropGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction != DataGridEditAction.Commit) return;
        if (e.Column?.Header?.ToString() != "Value") return;
        if (e.Row.Item is not DataRowView view) return;

        if ((view["__editable__"] as string) != "1")
        {
            Log($"{view["Property"]}: this property type isn't editable", "err");
            return;
        }

        int idx = int.Parse((string)view["__idx__"], CultureInfo.InvariantCulture);
        var (path, node) = _allProps[idx];
        string before = node.DisplayValue();
        string text = (e.EditingElement as TextBox)?.Text ?? "";

        if (node.TrySetFromText(text))
        {
            string after = node.DisplayValue();
            // normalise the visible cell to the canonical value once the commit settles
            Dispatcher.BeginInvoke(new Action(() => { try { view["Value"] = after; } catch { } }),
                                   System.Windows.Threading.DispatcherPriority.Background);
            Log($"{path}: {before} → {after}" + (after == before ? "  (unchanged)" : ""), "ok");
        }
        else
        {
            e.Cancel = true;   // reject the commit
            Dispatcher.BeginInvoke(new Action(() => { try { view["Value"] = before; } catch { } }),
                                   System.Windows.Threading.DispatcherPriority.Background);
            Log($"{path}: '{text}' is not a valid {node.TypeName}", "err");
        }
    }

    // ============================================================ save state (section 1, BXML)
    void BuildSaveState()
    {
        StringGrid.ItemsSource = null;
        SetXmlPreview("");
        if (StringSearch != null) StringSearch.Text = "";

        var bx = _container?.SaveState;
        if (bx == null)
        {
            SaveTitle.Text = "TEXT VALUES   ·   read-only (this section couldn't be parsed)";
            Log("save state: section 1 (BXML) couldn't be safely parsed — it won't be editable", "info");
            return;
        }
        SaveTitle.Text = $"TEXT VALUES   ·   {bx.Strings.Count:n0} strings";
        try { SetXmlPreview(bx.ToXml()); }
        catch (Exception ex) { SetXmlPreview("(could not render structure: " + ex.Message + ")"); }
        RenderStrings();
        Log($"save state: {bx.Strings.Count:n0} text values", "ok");
    }

    void RenderStrings()
    {
        var bx = _container?.SaveState;
        if (bx == null) { StringGrid.ItemsSource = null; return; }
        string term = (StringSearch.Text ?? "").Trim().ToLowerInvariant();

        var dt = new DataTable();
        dt.Columns.Add("__idx__", typeof(string));
        dt.Columns.Add("#", typeof(string));
        dt.Columns.Add("Text", typeof(string));
        for (int i = 0; i < bx.Strings.Count; i++)
        {
            string v = bx.Strings[i];
            if (term.Length > 0 && !v.ToLowerInvariant().Contains(term)) continue;
            var row = dt.NewRow();
            row["__idx__"] = i.ToString(CultureInfo.InvariantCulture);
            row["#"] = i.ToString(CultureInfo.InvariantCulture);
            row["Text"] = v;
            dt.Rows.Add(row);
        }
        StringGrid.ItemsSource = dt.DefaultView;
    }

    void StringSearch_Changed(object sender, TextChangedEventArgs e)
    {
        if (_container?.SaveState != null) RenderStrings();
    }

    void StringGrid_AutoGenColumn(object sender, DataGridAutoGeneratingColumnEventArgs e)
    {
        switch (e.PropertyName)
        {
            case "__idx__": e.Cancel = true; break;
            case "#":
                e.Column.IsReadOnly = true;
                e.Column.Width = new DataGridLength(0.18, DataGridLengthUnitType.Star); break;
            case "Text":
                e.Column.Width = new DataGridLength(1, DataGridLengthUnitType.Star); break;
        }
    }

    void StringGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction != DataGridEditAction.Commit) return;
        if (e.Column?.Header?.ToString() != "Text") return;
        if (e.Row.Item is not DataRowView view) return;
        var bx = _container?.SaveState;
        if (bx == null) return;

        int idx = int.Parse((string)view["__idx__"], CultureInfo.InvariantCulture);
        string before = bx.Strings[idx];
        string text = (e.EditingElement as TextBox)?.Text ?? "";
        bx.SetString(idx, text);
        Log($"save-state text #{idx}: \"{Trunc(before)}\" → \"{Trunc(text)}\"" + (before == text ? "  (unchanged)" : ""), "ok");
        Dispatcher.BeginInvoke(new Action(() => { try { SetXmlPreview(bx.ToXml()); } catch { } }),
                               System.Windows.Threading.DispatcherPriority.Background);
    }

    static readonly Regex XmlMarkup = new(@"<!--[\s\S]*?-->|<[^>]+>", RegexOptions.Compiled);
    static readonly Regex XmlTagName = new(@"^(?<open></?|<\?)(?<name>[^\s>/]+)", RegexOptions.Compiled);
    static readonly Regex XmlAttribute = new(@"(?<attr>[A-Za-z_:][\w:.-]*)(?<eq>\s*=\s*)(?<value>""[^""]*""|'[^']*')|(?<close>/?>|\?>)", RegexOptions.Compiled);

    void SetXmlPreview(string xml)
    {
        var document = new FlowDocument
        {
            PagePadding = new Thickness(0),
            LineHeight = 18,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            PageWidth = Math.Clamp(xml.Split('\n').Select(line => line.Length).DefaultIfEmpty(0).Max() * 7.4 + 40, 1200, 100000)
        };
        var paragraph = new Paragraph { Margin = new Thickness(0) };
        document.Blocks.Add(paragraph);
        int position = 0;
        foreach (Match markup in XmlMarkup.Matches(xml))
        {
            if (markup.Index > position)
                paragraph.Inlines.Add(new Run(xml.Substring(position, markup.Index - position)) { Foreground = BrDim });
            AddHighlightedXmlMarkup(paragraph, markup.Value);
            position = markup.Index + markup.Length;
        }
        if (position < xml.Length)
            paragraph.Inlines.Add(new Run(xml[position..]) { Foreground = BrDim });
        XmlPreview.Document = document;
    }

    static void AddHighlightedXmlMarkup(Paragraph paragraph, string markup)
    {
        if (markup.StartsWith("<!--", StringComparison.Ordinal))
        {
            paragraph.Inlines.Add(new Run(markup) { Foreground = BrXmlComment });
            return;
        }
        Match name = XmlTagName.Match(markup);
        int position = 0;
        if (name.Success)
        {
            paragraph.Inlines.Add(new Run(name.Groups["open"].Value) { Foreground = BrDim });
            paragraph.Inlines.Add(new Run(name.Groups["name"].Value) { Foreground = BrXmlName, FontWeight = FontWeights.SemiBold });
            position = name.Length;
        }
        foreach (Match token in XmlAttribute.Matches(markup, position))
        {
            if (token.Index > position)
                paragraph.Inlines.Add(new Run(markup.Substring(position, token.Index - position)) { Foreground = BrDim });
            if (token.Groups["attr"].Success)
            {
                paragraph.Inlines.Add(new Run(token.Groups["attr"].Value) { Foreground = BrXmlAttr });
                paragraph.Inlines.Add(new Run(token.Groups["eq"].Value) { Foreground = BrDim });
                paragraph.Inlines.Add(new Run(token.Groups["value"].Value) { Foreground = BrXmlValue });
            }
            else paragraph.Inlines.Add(new Run(token.Value) { Foreground = BrDim });
            position = token.Index + token.Length;
        }
        if (position < markup.Length)
            paragraph.Inlines.Add(new Run(markup[position..]) { Foreground = BrDim });
    }

    static string Trunc(string s) => s.Length <= 40 ? s : s.Substring(0, 40) + "…";

    // ============================================================ career (section 2, binary)
    void BuildCareer()
    {
        CareerGrid.ItemsSource = null;
        XuidBox.IsEnabled = true;
        XuidApplyBtn.IsEnabled = false;

        var bc = _container?.Career;
        if (bc == null)
        {
            CareerTitle.Text = "CAREER RECORDS   ·   read-only (this section couldn't be parsed)";
            XuidBox.Text = "";
            XuidBox.IsEnabled = false;
            XuidHint.Text = "account ID not available for this save";
            Log("career: section 2 (binary) couldn't be safely parsed", "info");
            return;
        }
        CareerTitle.Text = $"CAREER RECORDS   ·   {bc.Records.Count:n0} records";
        if (bc.HasXuid)
        {
            XuidBox.Text = bc.Xuid.ToString(CultureInfo.InvariantCulture);
            XuidApplyBtn.IsEnabled = true;
            XuidHint.Text = "the Xbox account this save belongs to";
        }
        else
        {
            XuidBox.Text = "(none)";
            XuidBox.IsEnabled = false;
            XuidHint.Text = "this save has no account ID";
        }

        var dt = new DataTable();
        dt.Columns.Add("#", typeof(string));
        dt.Columns.Add("Record", typeof(string));
        dt.Columns.Add("Stored as", typeof(string));
        dt.Columns.Add("Size", typeof(string));
        foreach (var r in bc.Records)
        {
            var row = dt.NewRow();
            row["#"] = r.Ordinal.ToString(CultureInfo.InvariantCulture);
            row["Record"] = r.Name;
            row["Stored as"] = r.Serializer;
            row["Size"] = $"{r.Payload.Length:n0} B";
            dt.Rows.Add(row);
        }
        CareerGrid.ItemsSource = dt.DefaultView;
        Log($"career: {bc.Records.Count:n0} records" + (bc.HasXuid ? $", account ID {bc.Xuid}" : ""), "ok");
    }

    void CareerGrid_AutoGenColumn(object sender, DataGridAutoGeneratingColumnEventArgs e)
    {
        switch (e.PropertyName)
        {
            case "#":         e.Column.Width = new DataGridLength(0.2, DataGridLengthUnitType.Star); break;
            case "Record":    e.Column.Width = new DataGridLength(1.3, DataGridLengthUnitType.Star); break;
            case "Stored as": e.Column.Width = new DataGridLength(1.3, DataGridLengthUnitType.Star); break;
            case "Size":      e.Column.Width = new DataGridLength(0.4, DataGridLengthUnitType.Star); break;
        }
    }

    void ApplyXuid_Click(object sender, RoutedEventArgs e)
    {
        var bc = _container?.Career;
        if (bc == null || !bc.HasXuid) return;
        string text = (XuidBox.Text ?? "").Trim();
        if (!ulong.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong xuid))
        {
            Log($"account ID: '{text}' is not a valid number", "err");
            return;
        }
        bc.SetXuid(xuid);
        Log($"account ID set to {xuid} — click Save edited to write it", "ok");
    }

    // ============================================================ overview (friendly landing)
    void BuildOverview()
    {
        OverviewPanel.Children.Clear();
        if (_container == null) return;

        // ---- Your save at a glance ----
        OverviewPanel.Children.Add(SectionHeader("Your save at a glance"));
        var chips = new WrapPanel { Margin = new Thickness(0, 4, 0, 4) };
        foreach (var (table, label) in new[]
                 {
                     ("Career_Garage", "Cars in garage"),
                     ("FreeCars", "Free cars"),
                     ("BarnFinds", "Barn finds"),
                     ("Career_PurchasedParts", "Purchased parts"),
                     ("PhotoCaptures", "Photos"),
                 })
        {
            long n = TableCountOrNeg(table);
            if (n >= 0) chips.Children.Add(StatChip(n.ToString("n0", CultureInfo.InvariantCulture), label));
        }
        if (_container.Properties != null)
            chips.Children.Add(StatChip(_allProps.Count(p => p.Node.IsEditable).ToString("n0", CultureInfo.InvariantCulture), "editable settings"));
        var career = _container.Career;
        OverviewPanel.Children.Add(chips);

        // ---- Everyday values ----
        OverviewPanel.Children.Add(SectionHeader("Everyday values"));
        int everyday = 0;
        everyday += AddProfileEdit("/Main/Credits", "Credits", "your spendable in-game credits") ? 1 : 0;
        everyday += AddProfileEdit("/Main/Level", "Driver level", "your current driver level") ? 1 : 0;
        everyday += AddProfileEdit("/Main/XP", "Driver XP", "experience earned toward driver progression") ? 1 : 0;
        everyday += AddBxmlIntegerEdit("CarPerkSaveState", "UnspentSkillPointsTotal", "Unspent skill points",
            "skill points currently available to spend") ? 1 : 0;
        everyday += AddBxmlIntegerEdit("CarPerkSaveState", "SkillPointsTotal", "Lifetime skill points",
            "total skill points earned across this profile") ? 1 : 0;
        if (everyday == 0)
            OverviewPanel.Children.Add(Note("This save does not expose the usual credits, level, XP or skill-point fields in a safely editable format."));

        // ---- Festival Playlist ----
        OverviewPanel.Children.Add(SectionHeader("Festival Playlist"));
        bool currentPlaylist = AddSeasonPlaylistEdit();
        bool playlistHistory = AddBxmlIntegerEdit(
            "CurrencyBucket_CollectionCategoryProgress_cc_festival_playlist", "Total",
            "Playlist history points",
            "lifetime Festival Playlist points used by the Playlist History reward track");
        if (playlistHistory)
        {
            var track = FindSaveMapValue("progression_thread_cc_festival_playlist");
            string level = GetBxmlPropertyValue(track, "Level") ?? "?";
            string checkpoint = GetBxmlPropertyValue(track, "CurrencyAtLastObjective") ?? "?";
            int claimed = GetBxmlPropertyNode(track, "ClaimedRewardObjectiveIds")?.Children.Count ?? 0;
            var playlistChips = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            playlistChips.Children.Add(StatChip(level, "Playlist reward level"));
            playlistChips.Children.Add(StatChip(checkpoint, "Last reward checkpoint"));
            playlistChips.Children.Add(StatChip(claimed.ToString("n0", CultureInfo.InvariantCulture), "Rewards claimed"));
            OverviewPanel.Children.Add(playlistChips);
        }
        if (currentPlaylist || playlistHistory)
        {
            OverviewPanel.Children.Add(Note("Current Season points are the score shown on the in-game Season Progress bar. History points are the separate lifetime total. "
                + "Completed events, reward checkpoints and claimed rewards are preserved and are not changed automatically."));
        }
        else
        {
            OverviewPanel.Children.Add(Note("No safely recognized Festival Playlist counters were found in this save. Playlist activity data is still preserved."));
        }

        // ---- Horizon Collection ----
        OverviewPanel.Children.Add(SectionHeader("Horizon Collection progress"));
        int collectionFields = 0;
        collectionFields += AddBxmlIntegerEdit("CurrencyBucket_FestivalCollectionCampaignProgress_cc_festival", "Total",
            "Overall Festival progress", "total progress across the Horizon Collection") ? 1 : 0;
        collectionFields += AddBxmlIntegerEdit("CurrencyBucket_CollectionCategoryProgress_cc_horizon_legend", "Total",
            "Horizon Legend progress", "progress in the Horizon Legend collection category") ? 1 : 0;
        collectionFields += AddBxmlIntegerEdit("CurrencyBucket_CollectionCategoryProgress_cc_car_collection", "Total",
            "Car Collection progress", "points earned from building your car collection") ? 1 : 0;
        collectionFields += AddBxmlIntegerEdit("CurrencyBucket_CollectionCategoryProgress_cc_horizon_promo", "Total",
            "Horizon Promo progress", "points earned by photographing cars") ? 1 : 0;
        collectionFields += AddBxmlIntegerEdit("CurrencyBucket_CollectionCategoryProgress_cc_drift_attack", "Total",
            "Drift Attack progress", "points earned through Drift Attack activities") ? 1 : 0;
        collectionFields += AddBxmlIntegerEdit("CurrencyBucket_DiscoveryCollectionCampaignProgress_cc_discovery", "Total",
            "Discovery progress", "points earned from exploring the world") ? 1 : 0;
        if (collectionFields == 0)
            OverviewPanel.Children.Add(Note("No readable Horizon Collection counters were found in this save."));
        else
            OverviewPanel.Children.Add(Note("These totals feed the matching reward tracks. Large jumps may pass several reward checkpoints; "
                + "claim those rewards normally in-game so the reward history stays consistent."));

        // ---- World and unlocks ----
        OverviewPanel.Children.Add(SectionHeader("World and unlocks"));
        var worldChips = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        long barnFinds = TableCountOrNeg("BarnFinds");
        if (barnFinds >= 0) worldChips.Children.Add(StatChip(barnFinds.ToString("n0", CultureInfo.InvariantCulture), "Barn finds saved"));
        long carUnlocks = TableCountOrNeg("CarExperienceUnlocks");
        if (carUnlocks >= 0) worldChips.Children.Add(StatChip(carUnlocks.ToString("n0", CultureInfo.InvariantCulture), "Car experience unlocks"));
        var characterItems = career?.Records.FirstOrDefault(record => record.Name == "CharacterCustomisation");
        if (characterItems != null && CareerSaveStateInsights.TryGetCharacterItemCount(characterItems.Payload, out int characterItemCount))
            worldChips.Children.Add(StatChip(characterItemCount.ToString("n0", CultureInfo.InvariantCulture), "Character items owned"));
        var sites = FindSaveMapValuesByType("FestivalSiteState").ToList();
        if (sites.Count > 0)
        {
            int built = sites.Count(site => IsBxmlTrue(GetBxmlPropertyValue(site, "Built")));
            int visited = sites.Count(site => IsBxmlTrue(GetBxmlPropertyValue(site, "Visited")));
            worldChips.Children.Add(StatChip($"{built}/{sites.Count}", "Festival sites built"));
            worldChips.Children.Add(StatChip($"{visited}/{sites.Count}", "Festival sites visited"));
        }
        OverviewPanel.Children.Add(worldChips);

        if (career != null)
        {
            var available = new HashSet<string>(career.Records.Select(r => r.Name), StringComparer.Ordinal);
            var friendlyRecords = new[]
            {
                ("FestivalPassSaveState", "current Festival Playlist"),
                ("ForzathonDailyChallengeSaveState", "daily challenges"),
                ("ForzathonWeeklyChallengeSaveState", "weekly challenges"),
                ("SeasonalPRStuntsSaveState", "seasonal PR stunts"),
                ("SeasonalHorizonLifeEventsSaveState", "seasonal events"),
                ("TreasureHuntChallengeSaveState", "Treasure Hunts"),
                ("PhotoChallengeSaveState", "Photo Challenges"),
                ("BadgeChallengeSaveState", "badges"),
                ("CarHornSaveState", "car horns"),
                ("EmoteSaveState", "emotes"),
            }.Where(item => available.Contains(item.Item1)).Select(item => item.Item2).ToList();
            if (friendlyRecords.Count > 0)
                OverviewPanel.Children.Add(Note("This save also contains: " + string.Join(", ", friendlyRecords) + ". "
                    + "Those systems are easy to identify but their inner data is not safely editable yet; the Career view keeps the advanced records available."));
        }

        // ---- Account binding and advanced tools ----
        OverviewPanel.Children.Add(SectionHeader("Save account"));
        if (career != null && career.HasXuid)
        {
            OverviewPanel.Children.Add(EditRow("Account ID (XUID)", "only change this when moving a save to another Xbox account",
                career.Xuid.ToString(CultureInfo.InvariantCulture),
                text =>
                {
                    if (!ulong.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong x))
                        return (false, "not a valid number");
                    career.SetXuid(x);
                    return (true, x.ToString(CultureInfo.InvariantCulture));
                }));
        }

        OverviewPanel.Children.Add(Note("Advanced users still have the full Profile Values, Save State XML, Career records, and Cars & Data views. "
            + "Nothing is written to disk until you click Save edited."));
    }

    bool AddProfileEdit(string path, string label, string hint)
    {
        var match = _allProps.FirstOrDefault(pair => string.Equals(pair.Path, path, StringComparison.OrdinalIgnoreCase));
        var node = match.Node;
        if (node == null || !node.IsEditable) return false;
        OverviewPanel.Children.Add(EditRow(label, hint, node.DisplayValue(),
            text => node.TrySetFromText(text)
                ? (true, node.DisplayValue())
                : (false, $"enter a valid {FriendlyType(node)}")));
        return true;
    }

    bool AddBxmlIntegerEdit(string mapKey, string propertyId, string label, string hint)
    {
        var bx = _container?.SaveState;
        var mapValue = FindSaveMapValue(mapKey);
        var property = GetBxmlPropertyNode(mapValue, propertyId);
        string current = GetBxmlPropertyValue(mapValue, propertyId);
        if (bx == null || property == null || current == null) return false;

        OverviewPanel.Children.Add(EditRow(label, hint, current,
            text =>
            {
                if (!int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) || value < 0)
                    return (false, "enter a whole number from 0 to 2,147,483,647");
                string normalized = value.ToString(CultureInfo.InvariantCulture);
                if (!bx.SetAttribute(property, "value", normalized)) return (false, "this value could not be updated");
                RefreshSaveStateDisplays();
                return (true, normalized);
            }));
        return true;
    }

    bool AddSeasonPlaylistEdit()
    {
        var career = _container?.Career;
        var record = career?.Records.FirstOrDefault(item => item.Name == "FestivalPassSaveState");
        if (career == null || record == null || !CareerSaveStateInsights.TryGetSeasonPoints(record.Payload, out int current))
            return false;

        OverviewPanel.Children.Add(EditRow("Current Season points", "the score shown on the current Season Progress bar",
            current.ToString(CultureInfo.InvariantCulture),
            text =>
            {
                if (!int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) || value < 0)
                    return (false, "enter a whole number from 0 to 2,147,483,647");
                if (!CareerSaveStateInsights.TrySetSeasonPoints(record.Payload, value, out byte[] edited))
                    return (false, "this save's Playlist layout is not safely recognized");
                career.ReplacePayload(record, edited);
                return (true, value.ToString(CultureInfo.InvariantCulture));
            }));
        return true;
    }

    Bxml.Node FindSaveMapValue(string mapKey)
    {
        var bx = _container?.SaveState;
        if (bx == null) return null;
        foreach (var mapElement in bx.Root.Children)
        {
            if (BxmlNodeName(bx, mapElement) != "map_element") continue;
            var key = mapElement.Children.FirstOrDefault(node => BxmlNodeName(bx, node) == "key");
            if (key == null || !string.Equals(bx.GetAttribute(key, "value"), mapKey, StringComparison.Ordinal)) continue;
            return mapElement.Children.FirstOrDefault(node => BxmlNodeName(bx, node) == "value");
        }
        return null;
    }

    IEnumerable<Bxml.Node> FindSaveMapValuesByType(string typeName)
    {
        var bx = _container?.SaveState;
        if (bx == null) yield break;
        foreach (var mapElement in bx.Root.Children)
        {
            if (BxmlNodeName(bx, mapElement) != "map_element") continue;
            var value = mapElement.Children.FirstOrDefault(node => BxmlNodeName(bx, node) == "value");
            if (value != null && string.Equals(bx.GetAttribute(value, "type"), typeName, StringComparison.Ordinal))
                yield return value;
        }
    }

    Bxml.Node GetBxmlPropertyNode(Bxml.Node parent, string propertyId)
    {
        var bx = _container?.SaveState;
        if (bx == null || parent == null) return null;
        return parent.Children.FirstOrDefault(node => BxmlNodeName(bx, node) == "property" &&
            string.Equals(bx.GetAttribute(node, "id"), propertyId, StringComparison.Ordinal));
    }

    string GetBxmlPropertyValue(Bxml.Node parent, string propertyId)
    {
        var bx = _container?.SaveState;
        var property = GetBxmlPropertyNode(parent, propertyId);
        return bx == null || property == null ? null : bx.GetAttribute(property, "value");
    }

    static string BxmlNodeName(Bxml bx, Bxml.Node node) => bx.Strings[node.NameIndex];
    static bool IsBxmlTrue(string value) => string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) || value == "1";

    void RefreshSaveStateDisplays()
    {
        var bx = _container?.SaveState;
        if (bx == null) return;
        RenderStrings();
        try { SetXmlPreview(bx.ToXml()); } catch { }
    }

    long TableCountOrNeg(string table)
    {
        if (_conn == null || !_tables.Contains(table)) return -1;
        try { using var c = _conn.CreateCommand(); c.CommandText = $"SELECT COUNT(*) FROM {Quote(table)}"; return Convert.ToInt64(c.ExecuteScalar()); }
        catch { return -1; }
    }

    TextBlock SectionHeader(string text) => new()
    {
        Text = text.ToUpperInvariant(),
        Foreground = (Brush)FindResource("Pink"),
        FontWeight = FontWeights.SemiBold,
        FontSize = 12.5,
        Margin = new Thickness(0, 12, 0, 4),
    };

    Border StatChip(string value, string label)
    {
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Text = value, Foreground = (Brush)FindResource("Ink"), FontSize = 19, FontWeight = FontWeights.Bold });
        sp.Children.Add(new TextBlock { Text = label, Foreground = (Brush)FindResource("Dim"), FontSize = 11.5, Margin = new Thickness(0, 2, 0, 0) });
        return new Border
        {
            Background = (Brush)FindResource("Card2"),
            BorderBrush = (Brush)FindResource("Line"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 10, 16, 10),
            Margin = new Thickness(0, 0, 10, 10),
            MinWidth = 118,
            Child = sp,
        };
    }

    TextBlock Note(string text) => new()
    {
        Text = text,
        Foreground = (Brush)FindResource("Faint"),
        FontSize = 11.5,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 10, 0, 0),
    };

    Grid EditRow(string label, string hint, string current, Func<string, (bool ok, string msg)> apply)
    {
        var grid = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(new TextBlock { Text = label, Foreground = (Brush)FindResource("Ink"), FontSize = 13 });
        left.Children.Add(new TextBlock { Text = hint, Foreground = (Brush)FindResource("Faint"), FontSize = 11, Margin = new Thickness(0, 1, 0, 0) });
        Grid.SetColumn(left, 0); grid.Children.Add(left);

        var box = new TextBox
        {
            Style = (Style)FindResource("Input"), Width = 220, Height = 32, Text = current,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0),
        };
        Grid.SetColumn(box, 1); grid.Children.Add(box);

        var btn = new Button
        {
            Content = "Apply", Style = (Style)FindResource("PinkButton"),
            Padding = new Thickness(16, 6, 16, 6), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(btn, 2); grid.Children.Add(btn);

        btn.Click += (s, e) =>
        {
            var (ok, msg) = apply(box.Text ?? "");
            if (ok) { box.Text = msg; Log($"{label} → {msg}", "ok"); }
            else Log($"{label}: {msg}", "err");
        };
        return grid;
    }

    // ============================================================ save
    void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_container == null || _conn == null) { Log("load a profile first", "err"); return; }
        var dlg = new SaveFileDialog
        {
            Title = "Save the edited ProfileData",
            FileName = "C_ProfileData.edited",
            Filter = "ProfileData|*.*",
        };
        if (dlg.ShowDialog() != true) return;
        try { SaveProfile(dlg.FileName); }
        catch (Exception ex) { Fail("save", ex); }
    }

    void SaveProfile(string destination)
    {
        // flush the working database to disk and release the file handle
        try { using var cmd = _conn.CreateCommand(); cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)"; cmd.ExecuteNonQuery(); } catch { }
        CloseConnection();
        GC.Collect(); GC.WaitForPendingFinalizers();

        byte[] editedDb = File.ReadAllBytes(_sqlitePath);
        if (!(editedDb.Length >= 16 && Encoding.ASCII.GetString(editedDb, 0, 16) == "SQLite format 3\0"))
        {
            OpenConnection();
            throw new InvalidDataException("the edited database is not a valid SQLite file.");
        }

        _container.Database = editedDb;
        byte[] stream = _container.Serialize();

        byte[] output;
        if (_outerCryptoApplied)
        {
            output = ProfileData.Encrypt(stream, _originalBytes);
            // verify the round trip decrypts back to exactly what we packed
            byte[] check = ProfileData.Decrypt(output);
            if (!check.AsSpan().SequenceEqual(stream))
                throw new InvalidDataException("re-encrypted profile failed round-trip verification; nothing was written.");
        }
        else
        {
            output = stream;
        }

        File.WriteAllBytes(destination, output);
        OpenConnection();   // reopen so editing can continue

        Log($"saved {Path.GetFileName(destination)}  ·  {output.Length:n0} bytes  ·  {(_outerCryptoApplied ? "re-encrypted + verified" : "decrypted stream")}", "ok");
        StatusLine.Text = $"saved {Path.GetFileName(destination)}";
    }

    // ============================================================ helpers
    static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";

    static string DisplayValue(object v)
    {
        if (v == null || v is DBNull) return "";
        if (v is byte[] b) return $"<blob {b.Length}B>";
        return Convert.ToString(v, CultureInfo.InvariantCulture);
    }

    // Choose a storage type that keeps numeric columns numeric in SQLite.
    static object ParseCell(string text)
    {
        if (text.Length == 0) return "";
        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long l)) return l;
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)) return d;
        return text;
    }

    void Fail(string action, Exception ex)
    {
        Log($"{action} failed: {ex.Message}", "err");
        MessageBox.Show(ex.Message, "FH6 Profile Editor", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
