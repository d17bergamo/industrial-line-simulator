using System.Globalization;
using System.Windows;
using System.Windows.Threading;

namespace LineSimulator.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandled;
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show("Errore imprevisto:\n" + e.Exception.Message, "Industrial Line Simulator",
            MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
