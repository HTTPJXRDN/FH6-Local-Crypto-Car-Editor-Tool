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
        SummaryText.Text = $"{preview.NewRows:n0} new donor rows · {preview.ChangedRows:n0} different matching rows";
        DetailsText.Text = preview.Warnings.Count > 0
            ? "INCOMPATIBLE TABLES — merge disabled:\n" + string.Join("\n", preview.Warnings)
            : preview.KeylessTables.Count > 0
                ? "Tables without row keys are added by content, so both versions of a changed row may remain:\n" +
                  string.Join("\n", preview.KeylessTables)
                : "No schema incompatibilities found. New-only update rows will remain in the output.";
        MergeButton.IsEnabled = preview.Warnings.Count == 0;
        SafetyText.Text = preview.Warnings.Count > 0 ? "No partial merge will be written." :
            "Both inputs stay untouched; a new output is created.";
    }
    private void Title_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        else DragMove();
    }
    private void Merge_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
