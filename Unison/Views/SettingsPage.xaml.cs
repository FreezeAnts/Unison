using Microsoft.UI.Xaml.Controls;
using Unison.ViewModels;

namespace Unison.Views;

/// <summary>
/// Settings UI. Hosted in a ContentDialog by MainWindow. Binds to SettingsViewModel.
/// </summary>
public sealed partial class SettingsPage : UserControl
{
    public SettingsViewModel ViewModel { get; }

    public SettingsPage(SettingsViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }
}
