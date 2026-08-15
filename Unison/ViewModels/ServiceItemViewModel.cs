using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Unison.Models;

namespace Unison.ViewModels;

/// <summary>
/// One sidebar row. Bound by MainWindow. Created by MainViewModel from a ServiceDefinition.
/// </summary>
public sealed partial class ServiceItemViewModel : ObservableObject
{
    public ServiceItemViewModel(ServiceDefinition definition)
    {
        Definition = definition;
        Name = definition.Name;
        IconGlyph = definition.ServiceType == ServiceType.WebService
            ? "\uE774"
            : definition.Id switch
            {
                "outlook" => "\uE715",
                "teams" => "\uE716",
                _ => "\uE8A5"
            };
        if (File.Exists(definition.IconPath))
        {
            IconImagePath = definition.IconPath;
        }
    }

    public ServiceDefinition Definition { get; }

    public string Name { get; }

    public string IconGlyph { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasIconImage))]
    [NotifyPropertyChangedFor(nameof(IconImageVisibility))]
    [NotifyPropertyChangedFor(nameof(GlyphVisibility))]
    private string? _iconImagePath;

    [ObservableProperty]
    private ImageSource? _iconImage;

    public bool HasIconImage => !string.IsNullOrEmpty(IconImagePath);

    public Microsoft.UI.Xaml.Visibility IconImageVisibility =>
        HasIconImage ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    public Microsoft.UI.Xaml.Visibility GlyphVisibility =>
        HasIconImage ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private int _unreadCount;

    public bool HasUnread => UnreadCount > 0;

    public Microsoft.UI.Xaml.Visibility BadgeVisibility =>
        UnreadCount > 0 ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    partial void OnUnreadCountChanged(int value)
    {
        OnPropertyChanged(nameof(HasUnread));
        OnPropertyChanged(nameof(BadgeVisibility));
    }

    partial void OnIconImagePathChanged(string? value) => ApplyIconImage(value);

    private void ApplyIconImage(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            IconImage = null;
            return;
        }

        IconImage = new BitmapImage(new Uri(path));
    }
}
