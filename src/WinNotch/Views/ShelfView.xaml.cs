using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WinNotch.Models;
using WinNotch.Services;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using DataObject = System.Windows.DataObject;
using DataFormats = System.Windows.DataFormats;
using DragDropEffects = System.Windows.DragDropEffects;

namespace WinNotch.Views;

public partial class ShelfView : UserControl
{
    private ShelfService? _service;
    private Point _itemDragStart;
    private bool _itemDragStarted;

    public ShelfView()
    {
        InitializeComponent();
    }

    public void Bind(ShelfService service)
    {
        _service = service;
        ShelfItemsControl.ItemsSource = service.Items;
        service.ShelfChanged += OnShelfChanged;
        UpdateVisibility();
    }

    private void OnShelfChanged()
    {
        Dispatcher.Invoke(UpdateVisibility);
    }

    private void UpdateVisibility()
    {
        if (_service == null) return;

        bool hasItems = _service.HasItems;
        bool dragging = DropZoneOverlay.Visibility == Visibility.Visible;
        ShelfHeader.Visibility = hasItems && !dragging ? Visibility.Visible : Visibility.Collapsed;
        ShelfScroller.Visibility = hasItems && !dragging ? Visibility.Visible : Visibility.Collapsed;
        EmptyState.Visibility = hasItems || dragging ? Visibility.Collapsed : Visibility.Visible;
    }

    public void ShowDropZone()
    {
        DropZoneOverlay.Visibility = Visibility.Visible;
        UpdateVisibility();
    }

    public void HideDropZone()
    {
        DropZoneOverlay.Visibility = Visibility.Collapsed;
        UpdateVisibility();
    }

    private void OnClearAllClick(object sender, RoutedEventArgs e)
    {
        _service?.Clear();
    }

    private void OnItemMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is ShelfItem item)
        {
            // Track potential drag start
            _itemDragStart = e.GetPosition(fe);
            _itemDragStarted = false;

            // Double-click to open
            if (e.ClickCount == 2)
            {
                try
                {
                    Process.Start(new ProcessStartInfo(item.FilePath) { UseShellExecute = true });
                }
                catch { }
            }
        }
    }

    private void OnItemRightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is ShelfItem item)
        {
            var notchWindow = Window.GetWindow(this) as NotchWindow;
            var menu = notchWindow?.CreateStyledContextMenu() ?? new ContextMenu();

            var openItem = new MenuItem { Header = "Open" };
            openItem.Click += (_, _) =>
            {
                try { Process.Start(new ProcessStartInfo(item.FilePath) { UseShellExecute = true }); }
                catch { }
            };
            menu.Items.Add(openItem);

            var openFolderItem = new MenuItem { Header = "Show in Explorer" };
            openFolderItem.Click += (_, _) =>
            {
                try
                {
                    if (item.IsFolder)
                        Process.Start(new ProcessStartInfo("explorer.exe", item.FilePath));
                    else
                        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{item.FilePath}\""));
                }
                catch { }
            };
            menu.Items.Add(openFolderItem);

            menu.Items.Add(new Separator());

            var removeItem = new MenuItem { Header = "Remove" };
            removeItem.Click += (_, _) => _service?.Remove(item);
            menu.Items.Add(removeItem);

            notchWindow?.TrackContextMenu(menu);
            menu.IsOpen = true;
            e.Handled = true;
        }
    }

    /// <summary>
    /// Initiate drag-out so users can drag shelf items to other applications.
    /// Uses a drag threshold to prevent accidental drags when clicking.
    /// </summary>
    private void OnItemMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed &&
            sender is FrameworkElement fe &&
            fe.DataContext is ShelfItem item)
        {
            // Check if we've exceeded drag threshold
            if (!_itemDragStarted)
            {
                Point currentPos = e.GetPosition(fe);
                double dx = currentPos.X - _itemDragStart.X;
                double dy = currentPos.Y - _itemDragStart.Y;
                double distance = Math.Sqrt(dx * dx + dy * dy);

                // Use drag threshold of 5 pixels
                if (distance > 5)
                {
                    _itemDragStarted = true;
                    var data = new DataObject(DataFormats.FileDrop, new[] { item.FilePath });
                    data.SetData("Preferred DropEffect", new System.IO.MemoryStream(BitConverter.GetBytes(2)));
                    var notchWindow = Window.GetWindow(this) as NotchWindow;
                    notchWindow?.SetShelfDragInProgress(true);
                    try
                    {
                        var result = DragDrop.DoDragDrop(fe, data, DragDropEffects.Copy | DragDropEffects.Move);
                        if ((result & (DragDropEffects.Copy | DragDropEffects.Move)) != 0) _service?.Remove(item);
                    }
                    finally { notchWindow?.SetShelfDragInProgress(false); _itemDragStarted = false; }
                }
            }
        }
        else
        {
            _itemDragStarted = false;
        }
    }
}
