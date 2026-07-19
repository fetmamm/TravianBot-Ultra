using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TbotUltra.Desktop.Models;
using TbotUltra.Desktop.ViewModels;

namespace TbotUltra.Desktop.Views;

/// <summary>
/// Hero / Adventures panel. Owns the drag-and-drop scratch state for the
/// attribute priority list and routes button clicks back to the host
/// MainWindow's internal "Core" methods, which still hold the
/// service-bound logic (refresh stats / refresh adventures / queue
/// adventure). The panel reads its DataContext as a
/// <see cref="HeroViewModel"/> inherited from the host TabItem.
/// </summary>
public partial class HeroPanel : UserControl
{
    private Point _dragStart;
    private HeroAttributePriorityItem? _dragSource;
    private MainWindow? _hostCache;

    public HeroPanel()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Resolves the parent <see cref="MainWindow"/>. Returns <c>null</c> while
    /// the panel is detached from the visual tree (e.g. early in the load
    /// cycle); production calls happen from event handlers, after the panel
    /// is mounted under MainWindow.
    /// </summary>
    private MainWindow? Host
    {
        get
        {
            if (_hostCache is not null)
            {
                return _hostCache;
            }

            _hostCache = Window.GetWindow(this) as MainWindow;
            return _hostCache;
        }
    }

    private HeroViewModel? Vm => DataContext as HeroViewModel;

    private void HeroAttributePriorityItemsControl_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(HeroAttributePriorityItemsControl);
        _dragSource = FindHeroAttributePriorityItem(e.OriginalSource as DependencyObject);
    }

    private void HeroAttributePriorityItemsControl_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragSource is null)
        {
            return;
        }

        var position = e.GetPosition(HeroAttributePriorityItemsControl);
        var delta = position - _dragStart;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        DragDrop.DoDragDrop(HeroAttributePriorityItemsControl, _dragSource, DragDropEffects.Move);
    }

    private void HeroAttributePriorityItemsControl_Drop(object sender, DragEventArgs e)
    {
        if (Vm is not { } vm)
        {
            return;
        }

        if (!e.Data.GetDataPresent(typeof(HeroAttributePriorityItem)))
        {
            return;
        }

        if (e.Data.GetData(typeof(HeroAttributePriorityItem)) is not HeroAttributePriorityItem sourceItem)
        {
            return;
        }

        var targetItem = FindHeroAttributePriorityItem(e.OriginalSource as DependencyObject);
        var fromIndex = vm.AttributePriorityItems.IndexOf(sourceItem);
        if (fromIndex < 0)
        {
            return;
        }

        var toIndex = targetItem is null
            ? vm.AttributePriorityItems.Count - 1
            : vm.AttributePriorityItems.IndexOf(targetItem);
        if (toIndex < 0 || fromIndex == toIndex)
        {
            return;
        }

        vm.AttributePriorityItems.Move(fromIndex, toIndex);
        vm.UpdateOrders();
        Host?.PersistHeroPriorityToConfig();
    }

    private static HeroAttributePriorityItem? FindHeroAttributePriorityItem(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is FrameworkElement { DataContext: HeroAttributePriorityItem item })
            {
                return item;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return null;
    }

    private async void RefreshHeroStatsButton_Click(object sender, RoutedEventArgs e)
    {
        if (Host is not { } host)
        {
            return;
        }

        await host.GuardUiAsync(async () =>
        {
            RefreshHeroStatsButton.IsEnabled = false;
            try
            {
                await host.RefreshHeroStatsCoreAsync();
            }
            finally
            {
                RefreshHeroStatsButton.IsEnabled = true;
            }
        });
    }

    private async void RefreshAdventuresButton_Click(object sender, RoutedEventArgs e)
    {
        if (Host is not { } host)
        {
            return;
        }

        await host.GuardUiAsync(async () =>
        {
            RefreshAdventuresButton.IsEnabled = false;
            try
            {
                await host.RefreshAdventuresCoreAsync();
            }
            finally
            {
                RefreshAdventuresButton.IsEnabled = true;
            }
        });
    }

    private async void RefreshHeroHpButton_Click(object sender, RoutedEventArgs e)
    {
        if (Host is not { } host)
        {
            return;
        }

        await host.GuardUiAsync(async () =>
        {
            RefreshHeroHpButton.IsEnabled = false;
            try
            {
                await host.RefreshHeroHpCoreAsync();
            }
            finally
            {
                RefreshHeroHpButton.IsEnabled = true;
            }
        });
    }

    private async void RefreshHeroInventoryButton_Click(object sender, RoutedEventArgs e)
    {
        if (Host is not { } host)
        {
            return;
        }

        await host.GuardUiAsync(async () =>
        {
            RefreshHeroInventoryButton.IsEnabled = false;
            try
            {
                await host.RefreshHeroInventoryCoreAsync();
            }
            finally
            {
                RefreshHeroInventoryButton.IsEnabled = true;
            }
        });
    }

    private void HeroResourceSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        Host?.OpenHeroResourceSettingsFromHeroPanel();
    }
}
