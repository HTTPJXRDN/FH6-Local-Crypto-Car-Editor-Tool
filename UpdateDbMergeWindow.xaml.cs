using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;

namespace FH6LocalCryptoTool;

public partial class UpdateDbMergeWindow : Window
{
    public UpdateDbMergeWindow(UpdateDbMergePreview preview, string displayedUpdated, string displayedModded)
    {
        InitializeComponent();
        SourcesText.Text = $"Clean updated DB (top): {Path.GetFileName(displayedUpdated)}\n" +
                           $"Old modded DB (overlay): {Path.GetFileName(displayedModded)}";
        SourcesText.ToolTip = $"Updated: {displayedUpdated}\nModded: {displayedModded}";
        SummaryText.Text = $"{preview.NewRows:n0} additions · {preview.ChangedRows:n0} edited rows · {preview.DeletedRows:n0} deletions";
        DetailsText.Text = preview.Warnings.Count > 0
            ? "INCOMPATIBLE TABLES — merge disabled:\n" + string.Join("\n", preview.Warnings)
            : preview.KeylessTables.Count > 0
                ? $"Matched clean baseline stamp: {preview.BaselineVersion}\nUpdated stamp: {preview.UpdatedVersion}\n\n" +
                  "Keyless tables use exact-content additions/deletions (including duplicate counts). A changed official row cannot always be matched to its old content; review these tables:\n" +
                  string.Join("\n", preview.KeylessTables)
                : $"Matched clean baseline stamp: {preview.BaselineVersion}\nUpdated stamp: {preview.UpdatedVersion}\n\n" +
                  "No schema incompatibilities found. New-only update rows and untouched official values remain in the output.";
        MergeButton.IsEnabled = preview.Warnings.Count == 0;
        SafetyText.Text = preview.Warnings.Count > 0 ? "No partial merge will be written." :
            "All inputs and the clean reference stay untouched; a new output is created.";
    }
    private void Title_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        else DragMove();
    }
    private void Merge_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
