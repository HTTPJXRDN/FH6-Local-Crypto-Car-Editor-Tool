using System.Windows;

namespace FH6LocalCryptoTool;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        try { TempWorkspace.Initialize(); }
        catch (Exception ex) {
            MessageBox.Show("The temporary workspace could not be opened. No fallback to C: was used.\n\n" + ex.Message, "Forza Mod Tool", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1); return;
        }
        base.OnStartup(e);
    }
}
