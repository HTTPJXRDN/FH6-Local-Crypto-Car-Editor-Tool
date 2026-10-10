using System.Windows;

namespace FH6LocalCryptoTool;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        try { TempWorkspace.Initialize(); }
        catch (Exception ex) {
            MessageBox.Show("The Windows temporary folder could not be used for the tool's working files. Check its permissions and available space.\n\n" + ex.Message, "Forza Mod Tool", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1); return;
        }
        base.OnStartup(e);
    }
}
