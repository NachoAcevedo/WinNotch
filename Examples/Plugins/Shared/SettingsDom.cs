using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using WinNotch.Views;

namespace PersonalCards;

/// <summary>
/// Utilidades para reacomodar la pantalla de ajustes integrada de WinNotch
/// (InlineSettingsView) en tiempo de ejecución, sin modificar WinNotch.exe.
/// Una "fila" es el elemento hijo del panel de ajustes que contiene el control,
/// más las descripciones (TextBlock) que lo siguen inmediatamente.
/// </summary>
public static class SettingsDom
{
    /// <summary>Las vistas de ajustes (horizontal y vertical) de todas las ventanas abiertas.</summary>
    public static List<InlineSettingsView> FindSettingsViews()
    {
        var views = new List<InlineSettingsView>();
        foreach (Window window in Application.Current.Windows)
        {
            var fields = window.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            foreach (var field in fields)
            {
                if (field.FieldType == typeof(InlineSettingsView) && field.GetValue(window) is InlineSettingsView view)
                {
                    views.Add(view);
                }
            }
        }
        return views;
    }

    /// <summary>Panel que contiene la fila del control y el índice de la fila dentro de él.</summary>
    public static (Panel Panel, UIElement Row)? FindRow(InlineSettingsView view, string controlName)
    {
        if (view.FindName(controlName) is not FrameworkElement element)
        {
            return null;
        }

        FrameworkElement current = element;
        while (current.Parent is FrameworkElement parent)
        {
            // El panel de la sección es el primer Panel con varias filas.
            if (parent is Panel panel && panel.Children.Count > 3)
            {
                return (panel, current);
            }
            current = parent;
        }
        return null;
    }

    /// <summary>La fila más las descripciones que la siguen (TextBlocks que no son encabezados).</summary>
    public static List<UIElement> RowBlock(InlineSettingsView view, Panel panel, UIElement row)
    {
        var block = new List<UIElement> { row };
        int i = panel.Children.IndexOf(row) + 1;
        while (i < panel.Children.Count && panel.Children[i] is TextBlock text && !IsHeader(view, text))
        {
            block.Add(text);
            i++;
        }
        return block;
    }

    public static bool IsHeader(InlineSettingsView view, TextBlock text)
    {
        return view.TryFindResource("SectionHeader") is Style header && ReferenceEquals(text.Style, header);
    }

    /// <summary>Mueve la fila de <paramref name="controlName"/> justo después de la fila de <paramref name="anchorName"/>.</summary>
    public static bool MoveAfter(InlineSettingsView view, string controlName, string anchorName)
    {
        var source = FindRow(view, controlName);
        var anchor = FindRow(view, anchorName);
        if (source == null || anchor == null)
        {
            return false;
        }

        var block = RowBlock(view, source.Value.Panel, source.Value.Row);
        foreach (var element in block)
        {
            source.Value.Panel.Children.Remove(element);
        }

        var anchorBlock = RowBlock(view, anchor.Value.Panel, anchor.Value.Row);
        int index = anchor.Value.Panel.Children.IndexOf(anchorBlock[^1]) + 1;
        foreach (var element in block)
        {
            anchor.Value.Panel.Children.Insert(index++, element);
        }
        return true;
    }

    /// <summary>Inserta un encabezado de sección (mismo estilo que los integrados) antes de la fila del control.</summary>
    public static bool InsertHeaderBefore(InlineSettingsView view, string controlName, string title, string tag)
    {
        var target = FindRow(view, controlName);
        if (target == null)
        {
            return false;
        }

        var panel = target.Value.Panel;
        if (panel.Children.OfType<FrameworkElement>().Any(e => Equals(e.Tag, tag)))
        {
            return true;
        }

        var header = new TextBlock { Text = title, Tag = tag };
        if (view.TryFindResource("SectionHeader") is Style style)
        {
            header.Style = style;
        }
        panel.Children.Insert(panel.Children.IndexOf(target.Value.Row), header);
        return true;
    }

    /// <summary>Descripción de la jerarquía de un control, para diagnóstico en el log.</summary>
    public static string DescribeParents(InlineSettingsView view, string controlName)
    {
        if (view.FindName(controlName) is not FrameworkElement element)
        {
            return controlName + ": no encontrado";
        }
        var chain = new List<string>();
        FrameworkElement? current = element;
        while (current != null && current != view)
        {
            string count = current is Panel p ? "[" + p.Children.Count + "]" : "";
            chain.Add(current.GetType().Name + count);
            current = current.Parent as FrameworkElement;
        }
        return controlName + ": " + string.Join(" < ", chain);
    }
}
