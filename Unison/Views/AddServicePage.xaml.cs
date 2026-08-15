using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Unison.ViewModels;

namespace Unison.Views;

/// <summary>
/// Add Service search UI. Hosted in a ContentDialog by MainWindow. Binds to AddServiceViewModel.
/// </summary>
public sealed partial class AddServicePage : UserControl
{
    public AddServiceViewModel ViewModel { get; }

    public AddServicePage(AddServiceViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.RefreshInstalled();
    }

    private async void Results_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (IsFromGetAppButton(e.OriginalSource))
        {
            return;
        }

        ViewModel.Choose(e.ClickedItem as CatalogItemViewModel);
        await Task.CompletedTask;
    }

    private async void GetAppButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: CatalogItemViewModel item })
        {
            await ViewModel.OpenStoreAsync(item);
        }
    }

    private static bool IsFromGetAppButton(object? source)
    {
        for (var current = source as DependencyObject; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is Button { Tag: "GetApp" })
            {
                return true;
            }
        }

        return false;
    }

    private void AddCustomWebsite_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.TryAddCustomWebsite();
    }
}
