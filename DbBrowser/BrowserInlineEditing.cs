using System.Data;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace FH6LocalCryptoTool.DbBrowser;

public partial class DbBrowserView
{
    bool _committingInline;

    bool CommitInlineEdit() => _committingInline ||
        (RowsGrid.CommitEdit(DataGridEditingUnit.Cell, true) && RowsGrid.CommitEdit(DataGridEditingUnit.Row, true));

    void Rows_BeginningEdit(object sender, DataGridBeginningEditEventArgs e)
    {
        int index = RowsGrid.Columns.IndexOf(e.Column);
        e.Cancel = _busy || _database == null || _page?.Editable != true || index < 0 ||
            _page.Columns[index].Hidden != 0 || !DiscardCellDraft();
    }

    void Rows_PreparingCellForEdit(object sender, DataGridPreparingCellForEditEventArgs e)
    {
        int rowIndex = RowsGrid.Items.IndexOf(e.Row.Item), columnIndex = RowsGrid.Columns.IndexOf(e.Column);
        if (_page == null || rowIndex < 0 || e.EditingElement is not TextBox editor) return;
        // Use the full value, not the grid's abbreviated BLOB/multiline preview.
        if (e.EditingEventArgs is TextCompositionEventArgs typing) {
            editor.Text = typing.Text; editor.CaretIndex = editor.Text.Length;
        }
        else { editor.Text = BrowserValue.From(_page.Rows[rowIndex].Values[columnIndex]).Text; editor.SelectAll(); }
        Status.Text = "Edit directly: Enter, Tab or click away to save to the working copy; Esc cancels. Export as creates the edited file.";
    }

    void Rows_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction != DataGridEditAction.Commit || _committingInline) return;
        if (_database == null || _page?.Editable != true || _pageObject == null || e.EditingElement is not TextBox editor) { e.Cancel = true; return; }
        int rowIndex = RowsGrid.Items.IndexOf(e.Row.Item), index = RowsGrid.Columns.IndexOf(e.Column);
        if (rowIndex < 0 || rowIndex >= _page.Rows.Count || index < 0 || _page.Columns[index].Hidden != 0) { e.Cancel = true; return; }
        var row = _page.Rows[rowIndex]; var column = _page.Columns[index];
        // Leaving an untouched NULL empty retains NULL; an empty TEXT remains TEXT.
        var original = BrowserValue.From(row.Values[index]);
        if (editor.Text == original.Text) return;
        _committingInline = true;
        try {
            var value = BrowserValue.Inline(column, row.Values[index], editor.Text);
            var updated = _database.UpdateCellAndRead(_pageObject, row, column, value);
            Array.Copy(updated.Values, row.Values, row.Values.Length);
            Array.Copy(updated.Identity, row.Identity, row.Identity.Length);
            if (e.Row.Item is DataRowView display) {
                for (int i = 0; i < row.Values.Length; i++) display[i] = BrowserDatabase.Display(row.Values[i]);
                display.EndEdit();
            }
            editor.Text = BrowserValue.From(row.Values[index]).Text;
            editor.ClearValue(ToolTipProperty);
            _cell = (_pageObject, row, column); _lastCell = RowsGrid.CurrentCell; ShowCell(); UpdateButtons();
            AppendSqlLog($"Updated {_pageObject.Name}.{column.Name} (inline, parameterized {value.Kind} value)");
            Status.Text = "Cell updated in working copy. Write changes sets a checkpoint; Export as creates the edited file. Refresh reapplies filters/sorting.";
        }
        catch (Exception ex) {
            e.Cancel = true; editor.ToolTip = ex.Message;
            Status.Text = "Cell not saved: " + ex.Message + " Correct the value or press Esc to cancel.";
            AppendSqlLog("INLINE EDIT ERROR: " + ex.Message);
        }
        finally { _committingInline = false; }
    }
}
