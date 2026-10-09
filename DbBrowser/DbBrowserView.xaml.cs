using System.Data;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Data;
using Microsoft.Win32;

namespace FH6LocalCryptoTool.DbBrowser;

public partial class DbBrowserView : UserControl
{
    BrowserDatabase? _database;
    IReadOnlyList<BrowserObject> _objects = [];
    BrowserPage? _page;
    int _pageIndex;
    bool _refreshing, _busy, _loadedHook;
    CancellationTokenSource? _cancel;
    BrowserObject? Selected => Tables.SelectedItem as BrowserObject;
    Window OwnerWindow => Window.GetWindow(this);

    public DbBrowserView() { InitializeComponent(); UpdateButtons(); }
    void View_Loaded(object sender, RoutedEventArgs e)
    {
        // Use the host's current header theme, with local fallback brushes for
        // standalone previews/tests where MainWindow resources are unavailable.
        if (TryFindResource("CardHeadGrad") is Brush) BrowserHeader.SetResourceReference(Border.BackgroundProperty, "CardHeadGrad");
        if (TryFindResource("PinkAccentLine") is Brush) BrowserHeader.SetResourceReference(Border.BorderBrushProperty, "PinkAccentLine");
        if (_loadedHook) return; _loadedHook = true;
        OwnerWindow.Closing += (_, args) => {
            if(args.Cancel)return;
            if (_busy) { args.Cancel = true; _cancel?.Cancel(); Status.Text = "Cancelling operation. Close again when it finishes."; return; }
            if (!CommitInlineEdit()) { args.Cancel = true; return; }
            if(!DiscardCellDraft()){args.Cancel=true;return;}
            if (_database != null && (_database.Pending || _database.HasChanges) && !Confirm("Close DB Browser and discard its working copy? Export first to keep your edits.")) args.Cancel = true;
        };
        OwnerWindow.Closed += (_, _) => { _database?.Dispose(); _database = null; };
    }
    bool Confirm(string message) => MessageBox.Show(OwnerWindow, message, "DB Browser", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
    void UpdateButtons()
    {
        WriteButton.IsEnabled = RevertButton.IsEnabled = _database?.Pending == true;
        ExportButton.IsEnabled = _database != null;
        DatabaseArea.IsEnabled = _database != null;
    }
    async Task Run(string description, Func<CancellationToken, Task> action, bool allowCancel = true)
    {
        if (_busy) return;
        if (!CommitInlineEdit()) return;
        if (_cellDirty && !description.StartsWith("Applying cell", StringComparison.Ordinal) && !DiscardCellDraft()) return;
        _busy = true; WorkArea.IsEnabled = false; CancelButton.Visibility = allowCancel ? Visibility.Visible : Visibility.Collapsed;
        using var cancellation = new CancellationTokenSource(); _cancel = cancellation;
        Status.Text = description;
        try { await action(cancellation.Token); }
        catch (Exception ex) {
            AppendSqlLog("ERROR: " + ex.Message);
            Status.Text = "Failed: " + ex.Message;
            MessageBox.Show(OwnerWindow, ex.Message + "\n\nThe input file was not changed.", "DB Browser", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { _busy = false; _cancel = null; WorkArea.IsEnabled = true; CancelButton.Visibility = Visibility.Collapsed; UpdateButtons(); }
    }
    async void Open_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Open database in DB Browser", Filter = "SLT or SQLite database|*.slt;*.sqlite;*.db;*.sqlite3|All files|*.*" };
        if (dialog.ShowDialog(OwnerWindow) == true) await LoadDatabase(dialog.FileName);
    }
    async Task LoadDatabase(string path)
    {
        if (_busy || !CommitInlineEdit()) return;
        if (_database != null && (_database.Pending || _database.HasChanges) && !Confirm("Open another database and discard the current working copy? Export first to keep edits.")) return;
        await Run("Opening private working copy…", async token => {
            BrowserDatabase? next = await Task.Run(() => BrowserDatabase.Open(path));
            try {
                var objects = await Task.Run(next.Objects);
                token.ThrowIfCancellationRequested();
                _database?.Dispose(); _database = next; next = null;
                _objects = objects; LoadedPath.Text = path; TableSearch.Text = ""; RowSearch.Text = ""; _pageIndex = 0; _columnFilters.Clear(); ClearCell();
                RefreshObjects(null); await LoadPage(token);
                SqlResults.ItemsSource = null; SqlResults.Columns.Clear();
                Status.Text = $"Loaded {objects.Count:N0} schema objects. Edits are isolated from the original.";
            }
            finally { next?.Dispose(); }
        });
    }
    void File_DragOver(object sender, DragEventArgs e) { e.Effects = !_busy && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; }
    async void File_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (!_busy && e.Data.GetData(DataFormats.FileDrop) is string[] { Length: 1 } paths) await LoadDatabase(paths[0]);
    }
    void RefreshObjects(string? selected)
    {
        _refreshing = true;
        try {
            var choices = _objects.Where(o => o.Type is "table" or "view").Where(o => o.Name.Contains(TableSearch.Text, StringComparison.OrdinalIgnoreCase)).ToList();
            Tables.ItemsSource = choices; Tables.SelectedItem = choices.FirstOrDefault(o => o.Name == selected) ?? choices.FirstOrDefault();
            TableSelector.ItemsSource = choices; TableSelector.SelectedItem = Tables.SelectedItem;
            SchemaObjectsGrid.ItemsSource = _objects;
            BuildSchemaTree();
            ObjectCount.Text = $"{choices.Count} tables / views • {_objects.Count} schema objects";
        }
        finally { _refreshing = false; }
    }
    async void TableSearch_Changed(object sender, TextChangedEventArgs e)
    {
        if (Tables == null || _refreshing || _busy) return;
        string? selected = Selected?.Name; RefreshObjects(selected); _pageIndex = 0;
        if (_database != null) await Run("Filtering tables…", LoadPage);
    }
    async void Table_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshing || _busy || _database == null) return;
        if (_cellDirty && !DiscardCellDraft()) { _refreshing = true; Tables.SelectedItem = _pageObject; _refreshing = false; return; }
        _refreshing = true; TableSelector.SelectedItem = Tables.SelectedItem; _refreshing = false;
        _pageIndex = 0; RowSearch.Text = ""; SortColumn.ItemsSource = null; _columnFilters.Clear();
        await Run("Loading table…", LoadPage);
    }
    async Task LoadPage(CancellationToken token)
    {
        var obj = Selected;
        // Never retain a previous table's row identities after a failed/cancelled refresh.
        _page = null; _pageObject = null; ClearCell(); RowsGrid.IsReadOnly = true; RowsGrid.ItemsSource = null; RowsGrid.Columns.Clear();
        if (_database == null || obj == null) {
            _page = null; RowsGrid.ItemsSource = null; RowsGrid.Columns.Clear(); StructureGrid.ItemsSource = null; SchemaSql.Text = ""; PageLabel.Text = "No table selected"; return;
        }
        string search = RowSearch.Text; string? sort = (SortColumn.SelectedItem as BrowserColumn)?.Name; bool desc = Descending.IsChecked == true;
        var filters = new Dictionary<string,string>(_columnFilters);
        var page = await Task.Run(() => _database.Page(obj, _pageIndex, search, sort, desc, token, filters));
        if (_pageIndex > 0 && page.Rows.Count == 0) { _pageIndex = Math.Max(0, (int)((page.Count - 1) / BrowserDatabase.PageSize)); await LoadPage(token); return; }
        _page = page; _pageObject = obj; StructureGrid.ItemsSource = page.Columns;
        SchemaSql.Text = string.Join("\n\n", new[] { obj }.Concat(_objects.Where(o => o.Type is "index" or "trigger" && o.TableName == obj.Name)).Select(o => o.Sql));
        _refreshing = true;
        SortColumn.ItemsSource = page.Columns; SortColumn.SelectedItem = page.Columns.FirstOrDefault(c => c.Name == sort);
        _refreshing = false;
        var display = new DataTable();
        for (int i = 0; i < page.Columns.Count; i++) display.Columns.Add("c" + i, typeof(string)).Caption = page.Columns[i].Name;
        foreach (var row in page.Rows) display.Rows.Add(row.Values.Select(v => (object)BrowserDatabase.Display(v)).ToArray());
        SetGrid(RowsGrid, display);
        RowsGrid.IsReadOnly = !page.Editable;
        for (int i = 0; i < page.Columns.Count; i++) {
            RowsGrid.Columns[i].IsReadOnly = page.Columns[i].Hidden != 0;
            // The edit-ending handler writes typed values and canonical display text.
            // Never let the text binding write an unvalidated string into the row.
            ((DataGridTextColumn)RowsGrid.Columns[i]).Binding = new Binding("[c" + i + "]") {
                Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.Explicit
            };
        }
        AddColumnFilters();
        GoPage.Text = (_pageIndex + 1).ToString();
        var properties = await Task.Run(_database.Properties); PropertiesGrid.ItemsSource = properties;
        UserVersion.Text = properties.First(p=>p.Key=="user_version").Value; ApplicationId.Text = properties.First(p=>p.Key=="application_id").Value;
        long start = page.Rows.Count == 0 ? 0 : (long)_pageIndex * BrowserDatabase.PageSize + 1;
        PageLabel.Text = $"{start:N0}–{start + Math.Max(0, page.Rows.Count - 1):N0} of {page.Count:N0} • " + (page.Editable ? "Double-click a cell to edit" : "Read-only view / no safe row identity");
    }
    static void SetGrid(DataGrid grid, DataTable data)
    {
        grid.Columns.Clear();
        foreach (DataColumn column in data.Columns) grid.Columns.Add(new DataGridTextColumn {
            Header = column.Caption, Binding = new Binding("[" + column.ColumnName + "]"), MinWidth = 95, MaxWidth = 450, Width = DataGridLength.SizeToHeader
        });
        grid.ItemsSource = data.DefaultView;
    }
    async void SchemaObject_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshing || _busy || _database == null || SchemaObjectsGrid.SelectedItem is not BrowserObject obj) return;
        SchemaSql.Text = obj.Sql;
        await Run("Inspecting schema object…", async _ => {
            StructureGrid.ItemsSource = await Task.Run(() => _database.Columns(obj.Type is "table" or "view" ? obj.Name : obj.TableName));
            Status.Text = $"{obj.Type}: {obj.Name}";
        }, false);
    }
    async Task Reload(CancellationToken token)
    {
        string? selected = Selected?.Name;
        _objects = await Task.Run(_database!.Objects); RefreshObjects(selected); await LoadPage(token);
    }
    async void Refresh_Click(object sender, RoutedEventArgs e) { _pageIndex = 0; await Run("Refreshing…", async token => { await Reload(token); Status.Text = "Data refreshed."; }); }
    void Search_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { e.Handled = true; Refresh_Click(sender, e); } }
    async void Previous_Click(object sender, RoutedEventArgs e) { if (_pageIndex == 0) return; _pageIndex--; await Run("Loading previous page…", LoadPage); }
    async void Next_Click(object sender, RoutedEventArgs e) { if (_page == null || ((long)_pageIndex + 1) * BrowserDatabase.PageSize >= _page.Count) return; _pageIndex++; await Run("Loading next page…", LoadPage); }
    (BrowserObject Obj, BrowserRow Row, int Column)? CurrentCell()
    {
        if (_page?.Editable != true || Selected == null || _pageObject?.Name != Selected.Name) { Status.Text = "Select an editable table first. Views and tables without a safe key are read-only here."; return null; }
        int row = RowsGrid.Items.IndexOf(RowsGrid.CurrentCell.Item);
        // Column reordering changes DisplayIndex; mapping uses the actual generated binding's index instead.
        int column = RowsGrid.CurrentCell.Column == null ? -1 : RowsGrid.Columns.IndexOf(RowsGrid.CurrentCell.Column);
        if (row < 0 || row >= _page.Rows.Count || column < 0 || column >= _page.Columns.Count) { Status.Text = "Select a data cell first."; return null; }
        return (Selected, _page.Rows[row], column);
    }
    async void Edit_Click(object sender, RoutedEventArgs e)
    {
        var current = CurrentCell(); if (current == null || _database == null || _page == null) return;
        var (obj, row, index) = current.Value; var column = _page.Columns[index];
        if (column.Hidden != 0) { Status.Text = "Generated columns cannot be edited."; return; }
        var entry = ValueEntry.For(column, BrowserValue.From(row.Values[index]));
        var editor = new BrowserValueWindow("Edit cell • " + obj.Name + "." + column.Name, [entry], false, Resources) { Owner = OwnerWindow };
        if (editor.ShowDialog() != true) return;
        await Run("Updating cell…", async token => { await Task.Run(() => _database.UpdateCell(obj, row, column, entry.Value)); await LoadPage(token); Status.Text = "Cell updated. Write changes or export to keep it; Revert changes undoes pending edits."; });
    }
    async void Insert_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || !CommitInlineEdit()) return;
        if (Selected == null || _database == null || _page?.Editable != true || _pageObject?.Name != Selected.Name) { Status.Text = "Select an editable table first."; return; }
        var obj = Selected; var entries = _page.Columns.Where(c => c.Hidden == 0).Select(c => ValueEntry.For(c, new BrowserValue("DEFAULT", ""))).ToList();
        var editor = new BrowserValueWindow("New record • " + obj.Name, entries, true, Resources) { Owner = OwnerWindow };
        if (editor.ShowDialog() != true) return;
        await Run("Inserting record…", async token => { await Task.Run(() => _database.Insert(obj, entries.ToDictionary(v => v.Name, v => v.Value))); await LoadPage(token); Status.Text = "Record inserted in the working copy. Clear any filter to see it."; });
    }
    async void Delete_Click(object sender, RoutedEventArgs e)
    {
        await DeleteSelectedRecord(Confirm);
    }
    (BrowserObject Obj, BrowserRow Row)? CurrentRecord()
    {
        if (_page?.Editable != true || Selected == null || _pageObject?.Name != Selected.Name) { Status.Text = "Select an editable table first. Views and tables without a safe key are read-only here."; return null; }
        // A row-header selection need not have a current column. Use the actual
        // selection, never a stale CurrentCell left behind after deselection.
        object? item = RowsGrid.SelectedItem ?? RowsGrid.SelectedCells.FirstOrDefault().Item;
        int index = item == null ? -1 : RowsGrid.Items.IndexOf(item);
        if (index < 0 || index >= _page.Rows.Count) { Status.Text = "Select a record or data cell first."; return null; }
        return (Selected, _page.Rows[index]);
    }
    async Task DeleteSelectedRecord(Func<string, bool> confirm)
    {
        if (_busy || !CommitInlineEdit()) return;
        var current = CurrentRecord(); if (current == null || _database == null) return;
        var (obj, row) = current.Value;
        if (!confirm("Delete the selected record from " + obj.Name + "? Foreign keys are not enforced, so related rows will not be removed automatically. Revert changes can undo this before committing.")) return;
        await Run("Deleting record…", async token => { await Task.Run(() => _database.Delete(obj, row)); await LoadPage(token); Status.Text = "Record deleted from the working copy."; });
    }
    async void Sql_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteSql(SqlEditor.Text);
    }
    async Task ExecuteSql(string sql)
    {
        if (_database == null) return;
        await Run("Running SQL…", async token => {
            SqlResults.ItemsSource = null; SqlResults.Columns.Clear(); AppendSqlLog(sql);
            var result = await Task.Run(() => _database.RunSql(sql, token)); SetGrid(SqlResults, result.Results); SqlMessage.Text = result.Message;
            AppendSqlLog(result.Message);
            await Reload(token); Status.Text = result.Message + (_database.Pending ? " Pending edits can be reverted." : "");
        });
    }
    async void Write_Click(object sender, RoutedEventArgs e)
    {
        if (_database == null) return;
        await Run("Writing changes to working copy…", async _ => { await Task.Run(_database.WriteChanges); Status.Text = "Committed to the private working copy. Export as… creates your edited file; original stays untouched."; }, false);
    }
    async void Revert_Click(object sender, RoutedEventArgs e)
    {
        if (_database == null || !Confirm("Undo all pending changes since the last Write changes or export?")) return;
        await Run("Reverting pending changes…", async token => { await Task.Run(_database.RevertChanges); await Reload(token); SqlResults.ItemsSource = null; Status.Text = "Pending changes reverted."; });
    }
    async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_database == null) return; bool slt = GameDbSqliteBridge.IsSlt(_database.SourcePath);
        var dialog = new SaveFileDialog { Title = "Export a new edited database", Filter = "Encrypted GameDB SLT|*.slt|SQLite database|*.sqlite", FilterIndex = slt ? 1 : 2,
            FileName = Path.GetFileNameWithoutExtension(_database.SourcePath) + "_browser_modified" + (slt ? ".slt" : ".sqlite"), AddExtension = true, OverwritePrompt = true };
        if (dialog.ShowDialog(OwnerWindow) != true) return;
        string? template = null;
        if (GameDbSqliteBridge.IsSlt(dialog.FileName) && !slt) {
            var original = new OpenFileDialog { Title = "Choose original encrypted GameDB template", Filter = "Encrypted GameDB|*.slt" };
            if (original.ShowDialog(OwnerWindow) != true) return; template = original.FileName;
        }
        await Run("Exporting and validating new database…", async _ => { await Task.Run(() => _database.Export(dialog.FileName, template)); Status.Text = "Exported: " + dialog.FileName + ". SQLite integrity verified; game compatibility still needs testing."; }, false);
    }
    void Close_Click(object sender, RoutedEventArgs e)
    {
        if (_database == null) return;
        if (!CommitInlineEdit()) return;
        if ((_database.Pending || _database.HasChanges) && !Confirm("Discard this working copy? Export first to keep your edits.")) return;
        if (!DiscardCellDraft()) return;
        _database.Dispose(); _database = null; _objects = []; _page = null; _pageObject = null; ClearCell(); _columnFilters.Clear();
        _refreshing = true; Tables.ItemsSource = null; _refreshing = false;
        RowsGrid.ItemsSource = null; RowsGrid.Columns.Clear(); SchemaObjectsGrid.ItemsSource = null; StructureGrid.ItemsSource = null; SchemaSql.Clear(); SqlResults.ItemsSource = null; SqlResults.Columns.Clear();
        TableSelector.ItemsSource = null; SchemaTree.Items.Clear(); PropertiesGrid.ItemsSource = null;
        LoadedPath.Text = "Drop an SLT or SQLite database here to begin."; Status.Text = "Database closed. Original untouched."; UpdateButtons();
    }
    void Cancel_Click(object sender, RoutedEventArgs e) { _cancel?.Cancel(); Status.Text = "Cancellation requested…"; }
}

public sealed class ValueEntry : INotifyPropertyChanged
{
    string _kind = "TEXT", _text = "", _inferredKind = "TEXT";
    public event PropertyChangedEventHandler? PropertyChanged;
    public string Name { get; init; } = "";
    public string Declared { get; init; } = "";
    public string Kind
    {
        get => _kind;
        set {
            if (_kind == value) return;
            _kind = value;
            // An explicit DEFAULT/NULL choice must not retain an ignored draft.
            if (value is "DEFAULT" or "NULL") { _text = ""; PropertyChanged?.Invoke(this, new(nameof(Text))); }
            PropertyChanged?.Invoke(this, new(nameof(Kind)));
            PropertyChanged?.Invoke(this, new(nameof(Value)));
        }
    }
    public string Text
    {
        get => _text;
        set {
            if (_text == value) return;
            _text = value;
            // Untouched fields keep DEFAULT; typing a value makes it an actual
            // parameter using the column's affinity, without a separate click.
            if (value.Length > 0 && _kind is "DEFAULT" or "NULL") Kind = _inferredKind;
            PropertyChanged?.Invoke(this, new(nameof(Text)));
            PropertyChanged?.Invoke(this, new(nameof(Value)));
        }
    }
    public BrowserValue Value => new(Kind, Text);
    public static ValueEntry For(BrowserColumn column, BrowserValue value) => new() {
        Name = column.Name, Declared = column.Type + (column.NotNull ? " • NOT NULL" : "") + (column.Default != null ? " • default " + column.Default : ""), _inferredKind = BrowserValue.Inline(column, DBNull.Value, "").Kind, Kind = value.Kind, Text = value.Text
    };
}

sealed class BrowserValueWindow : Window
{
    public BrowserValueWindow(string title, IReadOnlyList<ValueEntry> entries, bool inserting, ResourceDictionary theme)
    {
        Title = title; Width = 820; Height = inserting ? 620 : 360; MinWidth = 520; MinHeight = 280;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; Background = new SolidColorBrush(Color.FromRgb(29,29,31));
        Foreground = Brushes.White; Resources.MergedDictionaries.Add(theme);
        var layout = new DockPanel { Margin = new Thickness(16) }; Content = layout;
        var heading = new TextBlock { Text = title, FontSize = 18, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(232,23,93)), Margin = new Thickness(0,0,0,10) };
        DockPanel.SetDock(heading, Dock.Top); layout.Children.Add(heading);
        var help = new TextBlock { Text = "Typing a value selects its column's storage type automatically; you can change the type manually. Untouched DEFAULT fields use SQLite defaults. Choosing DEFAULT or NULL clears the value. NULL is not the text ‘NULL’. BLOB values use hex bytes. Numbers accept dot or comma.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,0,0,10), Foreground = Brushes.LightGray };
        DockPanel.SetDock(help, Dock.Top); layout.Children.Add(help);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0,10,0,0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true }; actions.Children.Add(cancel);
        var save = new Button { Content = inserting ? "Insert record" : "Update cell", Background = new SolidColorBrush(Color.FromRgb(232,23,93)) }; actions.Children.Add(save);
        DockPanel.SetDock(actions, Dock.Bottom); layout.Children.Add(actions);
        var grid = new DataGrid { AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false, RowHeight = double.NaN, ItemsSource = entries };
        grid.Columns.Add(new DataGridTextColumn { Header = "Column / declared type", Binding = new Binding("Name"), IsReadOnly = true, Width = 190 });
        grid.Columns.Add(new DataGridComboBoxColumn { Header = "Storage type", ItemsSource = inserting ? new[] { "DEFAULT", "NULL", "INTEGER", "REAL", "TEXT", "BLOB" } : ["NULL", "INTEGER", "REAL", "TEXT", "BLOB"], SelectedItemBinding = new Binding("Kind") { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }, Width = 120 });
        var textEditor = new FrameworkElementFactory(typeof(TextBox));
        textEditor.SetBinding(TextBox.TextProperty, new Binding("Text") { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        textEditor.SetValue(TextBox.AcceptsReturnProperty, true); textEditor.SetValue(TextBox.TextWrappingProperty, TextWrapping.Wrap);
        textEditor.SetValue(TextBox.MinHeightProperty, 60.0); textEditor.SetValue(TextBox.MaxHeightProperty, 180.0);
        textEditor.SetValue(TextBox.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Auto);
        grid.Columns.Add(new DataGridTemplateColumn { Header = "Value (empty text is allowed)", CellTemplate = new DataTemplate { VisualTree = textEditor }, Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        grid.RowStyle = new Style(typeof(DataGridRow), (Style)theme[typeof(DataGridRow)]);
        grid.RowStyle.Setters.Add(new Setter(ToolTipProperty, new Binding("Declared")));
        layout.Children.Add(grid);
        save.Click += (_, _) => {
            if (!grid.CommitEdit(DataGridEditingUnit.Cell, true) || !grid.CommitEdit(DataGridEditingUnit.Row, true)) return;
            try { foreach (var entry in entries) if (entry.Kind != "DEFAULT") entry.Value.Parse(); DialogResult = true; }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Invalid value", MessageBoxButton.OK, MessageBoxImage.Warning); }
        };
    }
}
