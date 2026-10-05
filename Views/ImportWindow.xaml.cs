using System.Windows;
using ErogeLauncher.Services;

namespace ErogeLauncher.Views;

public partial class ImportWindow : Window
{
    public ImportScope Scope { get; private set; } = ImportScope.All;

    public ImportWindow()
    {
        InitializeComponent();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        Scope = PlayedOnlyRadio.IsChecked == true ? ImportScope.PlayedOnly
              : PossessionRadio.IsChecked == true ? ImportScope.PlayedAndPossession
              : ImportScope.All;
        DialogResult = true;
    }
}
