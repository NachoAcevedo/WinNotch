using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using WinNotch.Helpers;
using WinNotch.Plugins;

namespace PersonalCards;

/// <summary>
/// Tarjeta de Tareas conectada a Microsoft To Do a través de Outlook clásico:
/// muestra las tareas pendientes de todas las cuentas, permite completarlas
/// y agregar nuevas (en la cuenta por defecto de Outlook).
/// </summary>
public sealed class TasksCardView : UserControl
{
    private readonly TextBox _entry = new TextBox
    {
        MinWidth = 220.0,
        Margin = new Thickness(0.0, 0.0, 6.0, 0.0),
        ToolTip = "Nueva tarea"
    };

    private readonly StackPanel _list = new StackPanel();

    private readonly TextBlock _header = new TextBlock
    {
        FontSize = 10.0,
        Margin = new Thickness(0.0, 0.0, 0.0, 6.0)
    };

    private readonly TextBlock _status = new TextBlock
    {
        FontSize = 10.0,
        Margin = new Thickness(0.0, 5.0, 0.0, 0.0),
        TextWrapping = TextWrapping.Wrap
    };

    private readonly DispatcherTimer _timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(2.0) };
    private bool _busy;

    public TasksCardView()
    {
        var root = new DockPanel();
        var top = new StackPanel();

        _header.Text = "Microsoft To Do · Outlook";
        _header.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");
        top.Children.Add(_header);

        var entryRow = new DockPanel();
        var addButton = new Button
        {
            Content = "Agregar",
            Padding = new Thickness(10.0, 3.0, 10.0, 3.0)
        };
        DockPanel.SetDock(addButton, Dock.Right);
        entryRow.Children.Add(addButton);
        entryRow.Children.Add(_entry);
        top.Children.Add(entryRow);
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);

        DockPanel.SetDock(_status, Dock.Bottom);
        _status.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");
        root.Children.Add(_status);

        root.Children.Add(new ScrollViewer
        {
            Content = _list,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = 150.0
        });
        Content = root;

        addButton.Click += (_, _) => AddTask();
        _entry.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Return)
            {
                AddTask();
                e.Handled = true;
            }
        };
        _entry.PreviewMouseDown += (_, _) =>
        {
            var window = Window.GetWindow(this);
            if (window != null)
            {
                WindowHelper.EnableTextInput(window);
            }
        };

        _timer.Tick += (_, _) => Refresh();
        Loaded += (_, _) => { _timer.Start(); Refresh(); };
        Unloaded += (_, _) => _timer.Stop();
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true)
            {
                Refresh();
            }
        };
    }

    private async void Refresh()
    {
        if (_busy)
        {
            return;
        }
        _busy = true;
        try
        {
            var tasks = await OutlookTasks.RunSta(OutlookTasks.FetchPending);
            if (tasks == null)
            {
                _status.Text = "Abrí Outlook para ver tus tareas de To Do.";
                return;
            }
            Render(tasks);
            _status.Text = "Actualizado " + DateTime.Now.ToString("HH:mm");
        }
        catch (Exception ex)
        {
            _status.Text = "No se pudieron leer las tareas: " + ex.Message;
        }
        finally
        {
            _busy = false;
        }
    }

    private async void AddTask()
    {
        string title = _entry.Text.Trim();
        if (title.Length == 0)
        {
            return;
        }
        title = title.Substring(0, Math.Min(title.Length, 255));

        try
        {
            bool ok = await OutlookTasks.RunSta(() => OutlookTasks.Add(title));
            if (!ok)
            {
                _status.Text = "Abrí Outlook para agregar tareas.";
                return;
            }
            _entry.Clear();
            Refresh();
        }
        catch (Exception ex)
        {
            _status.Text = "No se pudo agregar la tarea: " + ex.Message;
        }
    }

    private async void CompleteTask(OutlookTask task, UIElement row)
    {
        row.IsEnabled = false;
        try
        {
            bool ok = await OutlookTasks.RunSta(() => OutlookTasks.Complete(task));
            if (!ok)
            {
                _status.Text = "Abrí Outlook para completar tareas.";
                row.IsEnabled = true;
                return;
            }
            _list.Children.Remove(row);
            Refresh();
        }
        catch (Exception ex)
        {
            _status.Text = "No se pudo completar la tarea: " + ex.Message;
            row.IsEnabled = true;
        }
    }

    private void Render(List<OutlookTask> tasks)
    {
        _list.Children.Clear();
        int accounts = tasks.Select(t => t.Account).Distinct().Count();
        _header.Text = $"Microsoft To Do · {tasks.Count} pendientes en {accounts} cuenta(s)";

        foreach (var task in tasks)
        {
            var row = new DockPanel { Margin = new Thickness(0.0, 5.0, 0.0, 0.0) };

            var dot = new Ellipse
            {
                Width = 6.0,
                Height = 6.0,
                Margin = new Thickness(0.0, 0.0, 6.0, 0.0),
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = task.Account
            };
            try
            {
                dot.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(task.Color));
            }
            catch
            {
                dot.SetResourceReference(Shape.FillProperty, "AccentBrush");
            }
            DockPanel.SetDock(dot, Dock.Left);
            row.Children.Add(dot);

            if (task.Due is DateTime due)
            {
                var dueText = new TextBlock
                {
                    Text = due.ToString("dd/MM"),
                    FontSize = 10.0,
                    Margin = new Thickness(6.0, 0.0, 0.0, 0.0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                dueText.SetResourceReference(TextBlock.ForegroundProperty,
                    due.Date < DateTime.Today ? "AccentBrush" : "TextMutedBrush");
                DockPanel.SetDock(dueText, Dock.Right);
                row.Children.Add(dueText);
            }

            var check = new CheckBox
            {
                Content = new TextBlock
                {
                    Text = task.IsMail ? "✉ " + task.Title : task.Title,
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 310.0
                },
                ToolTip = task.Account,
                VerticalContentAlignment = VerticalAlignment.Center
            };
            check.SetResourceReference(Control.ForegroundProperty, "TextPrimaryBrush");
            check.Click += (_, _) => CompleteTask(task, row);
            row.Children.Add(check);

            _list.Children.Add(row);
        }

        if (tasks.Count == 0)
        {
            var empty = new TextBlock
            {
                Text = "No tenés tareas pendientes.",
                Margin = new Thickness(0.0, 14.0, 0.0, 0.0)
            };
            empty.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");
            _list.Children.Add(empty);
        }
    }
}

[WinNotchPlugin("personal.card.tasks", "Tareas", "1.1.0", "Personal")]
public sealed class TasksCardPlugin : CardPluginBase
{
    public override string Id => "personal.card.tasks";

    public override string Name => "Tareas";

    public override string Version => "1.1.0";

    public override string Description => "Tarjeta de tareas de Microsoft To Do (todas las cuentas de Outlook clásico)";

    public override string CardKey => "tasks";

    public override string CardTitle => "Tareas";

    public override int CardOrder => 40;

    public override double CardHeight => 270.0;

    protected override UIElement CreateCard()
    {
        return new TasksCardView();
    }
}
