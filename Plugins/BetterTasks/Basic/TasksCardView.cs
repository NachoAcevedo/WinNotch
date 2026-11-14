using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WinNotch.Helpers;
using WinNotch.Services;

namespace WinNotch.Views;

internal sealed record LocalTaskItem(Guid Id, string Title, bool Done);

public sealed class TasksCardView : UserControl
{
    private readonly List<LocalTaskItem> _tasks = LocalCards.Read<List<LocalTaskItem>>("tasks.json") ?? new();
    private readonly TextBox _entry = new() { MinWidth = 220, Margin = new Thickness(0, 0, 6, 0), ToolTip = "Nueva tarea" };
    private readonly StackPanel _list = new();
    private readonly TextBlock _status = new() { FontSize = 10, Margin = new Thickness(0, 5, 0, 0), TextWrapping = TextWrapping.Wrap };

    public TasksCardView()
    {
        var root = new DockPanel();
        var heading = new StackPanel();
        var note = new TextBlock { Text = "Lista local · Microsoft To Do pendiente de conectar", FontSize = 10, Margin = new Thickness(0, 0, 0, 6) };
        note.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");
        heading.Children.Add(note);
        var editor = new DockPanel();
        var add = new Button { Content = "Agregar", Padding = new Thickness(10, 3, 10, 3) };
        DockPanel.SetDock(add, Dock.Right);
        editor.Children.Add(add);
        editor.Children.Add(_entry);
        heading.Children.Add(editor);
        DockPanel.SetDock(heading, Dock.Top);
        root.Children.Add(heading);
        DockPanel.SetDock(_status, Dock.Bottom);
        _status.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");
        root.Children.Add(_status);
        root.Children.Add(new ScrollViewer { Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 150 });
        Content = root;
        add.Click += (_, _) => AddTask();
        _entry.KeyDown += (_, e) => { if (e.Key == Key.Return) { AddTask(); e.Handled = true; } };
        _entry.PreviewMouseDown += (_, _) => { if (Window.GetWindow(this) is Window window) WindowHelper.EnableTextInput(window); };
        Render();
    }
    private void AddTask()
    {
        var text = _entry.Text.Trim();
        if (text.Length == 0) return;
        _tasks.Add(new(Guid.NewGuid(), text[..Math.Min(text.Length, 500)], false));
        if (Save()) _entry.Clear();
        Render();
    }
    private bool Save()
    {
        try { LocalCards.Write("tasks.json", _tasks); _status.Text = "Guardado en este equipo"; return true; }
        catch (Exception) { _status.Text = "No se pudo guardar. Las tareas siguen visibles en esta sesión."; return false; }
    }
    private void Render()
    {
        _list.Children.Clear();
        foreach (var task in _tasks.OrderBy(item => item.Done).ToArray())
        {
            var row = new DockPanel { Margin = new Thickness(0, 5, 0, 0) };
            var remove = new Button { Content = "×", Width = 24, ToolTip = "Quitar tarea" };
            DockPanel.SetDock(remove, Dock.Right);
            row.Children.Add(remove);
            var check = new CheckBox { Content = new TextBlock { Text = task.Title, TextWrapping = TextWrapping.Wrap, MaxWidth = 330 }, IsChecked = task.Done, VerticalContentAlignment = VerticalAlignment.Center };
            check.SetResourceReference(CheckBox.ForegroundProperty, "TextPrimaryBrush");
            check.Click += (_, _) => { var index = _tasks.FindIndex(item => item.Id == task.Id); if (index >= 0) _tasks[index] = task with { Done = check.IsChecked == true }; Save(); };
            remove.Click += (_, _) => { _tasks.RemoveAll(item => item.Id == task.Id); Save(); Render(); };
            row.Children.Add(check);
            _list.Children.Add(row);
        }
        if (_tasks.Count == 0)
        {
            var empty = new TextBlock { Text = "Agregá tu primera tarea.", Margin = new Thickness(0, 14, 0, 0) };
            empty.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");
            _list.Children.Add(empty);
        }
    }
}
