using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace LineSimulator.App;

/// <summary>Finestra non modale con velocità di linea libera (0,5 - 1,5 m/s) e produttività oraria.</summary>
public partial class SpeedWindow : Window
{
    public SpeedWindow(object viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void Box_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox tb) return;
        tb.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        e.Handled = true;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
