using System.Windows;
using System.Windows.Input;

namespace FH6CarEditor;

public partial class BatchEditConfirmWindow : Window
{
    public BatchEditConfirmWindow(string actionName, int carCount, bool powerSettings = false)
    {
        InitializeComponent();
        ActionText.Text = $"{actionName} for {carCount} selected cars";
        if (powerSettings)
        {
            DialogSection.Text = "FH6 LOCAL MOD TOOL  /  BATCH POWER EDIT";
            DialogHeading.Text = "Apply power settings to selected cars?";
            DetailsPrimary.Text = "The selected boost, redline, weight-distribution and EV torque settings are applied to the stock powertrains of the selected cars, where applicable.";
            DetailsSecondary.Text = "Each distinct engine or motor ID is updated once. Weight distribution remains specific to each selected car.";
            DetailsWarning.Text = "Engine and motor records are shared by ID. Unselected cars using the same IDs may also change.";
            ConfirmButton.Content = "Apply power settings";
        }
    }

    public BatchEditConfirmWindow(string carName, long upgradeId, long bodyId)
    {
        InitializeComponent();
        Title = "FH6 Local Mod Tool - Remove Widebody";
        DialogSection.Text = "FH6 LOCAL MOD TOOL  /  REMOVE WIDEBODY";
        DialogHeading.Text = "Remove this added widebody?";
        ActionText.Text = $"{carName}  ·  Upgrade PartId {upgradeId}  ·  CarBodyID {bodyId}";
        DetailsPrimary.Text = "The selected widebody upgrade, its body definition, and its body-specific upgrade rows will be removed from the editor's working database.";
        DetailsSecondary.Text = "The stock body, factory widebodies, and other added widebodies will remain. The selected tab will return to Stock body.";
        DetailsWarning.Text = "Your loaded source database is not changed. Export DB to save this removal to a new file; keep a backup before using it in game.";
        ConfirmButton.Content = "Remove widebody";
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Confirm_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
