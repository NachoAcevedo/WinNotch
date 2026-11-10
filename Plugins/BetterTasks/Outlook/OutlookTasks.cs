using System.Runtime.InteropServices;

namespace PersonalCards;

/// <summary>Tarea pendiente de Microsoft To Do, leída de Outlook clásico.</summary>
public sealed record OutlookTask(string EntryId, string StoreId, string Title, string Account, string Color, DateTime? Due, bool IsMail);

/// <summary>
/// Acceso a las tareas de To Do de todas las cuentas de Outlook clásico,
/// usando la sesión de Outlook ya abierta (COM), sin login ni autorizaciones.
/// Todos los métodos deben llamarse desde un hilo STA.
/// </summary>
public static class OutlookTasks
{
    private const int OlFolderTasks = 13;
    private const int OlFolderToDo = 28;
    private const int OlTaskItem = 3;
    private const int OlTaskClass = 48;
    private const int OlFlagComplete = 1;

    // Mismo orden de colores que el plugin de calendario multicuenta.
    private static readonly string[] AccountColors =
        { "#4A90D9", "#E5884F", "#5CB85C", "#B07CD8", "#D9534F", "#3BB3B3" };

    /// <summary>Devuelve null si Outlook no está abierto.</summary>
    public static List<OutlookTask>? FetchPending()
    {
        var outlook = GetActiveOutlook();
        if (outlook == null)
        {
            return null;
        }

        dynamic? ns = null;
        dynamic? stores = null;
        var all = new List<OutlookTask>();
        try
        {
            ns = ((dynamic)outlook).GetNamespace("MAPI");
            stores = ns.Stores;
            int count = stores.Count;
            for (int i = 1; i <= count; i++)
            {
                dynamic? store = null;
                try
                {
                    store = stores.Item(i);
                    string color = AccountColors[(i - 1) % AccountColors.Length];
                    all.AddRange(FetchStore(store, color));
                }
                catch
                {
                    // Stores sin carpeta To Do (PST, carpetas públicas, etc.)
                }
                finally
                {
                    ReleaseCom(store);
                }
            }
        }
        finally
        {
            ReleaseCom(stores);
            ReleaseCom(ns);
            ReleaseCom(outlook);
        }

        return all
            .OrderBy(t => t.Due ?? DateTime.MaxValue)
            .ThenBy(t => t.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static List<OutlookTask> FetchStore(dynamic store, string color)
    {
        dynamic? folder = null;
        dynamic? items = null;
        dynamic? restricted = null;
        var list = new List<OutlookTask>();
        try
        {
            string storeId = store.StoreID;
            string account = store.DisplayName;
            folder = store.GetDefaultFolder(OlFolderToDo);
            items = folder.Items;
            restricted = items.Restrict("[Complete] = False");

            foreach (dynamic item in restricted)
            {
                try
                {
                    int cls = item.Class;
                    DateTime due = item.TaskDueDate;
                    list.Add(new OutlookTask(
                        item.EntryID,
                        storeId,
                        (string?)item.TaskSubject ?? (string?)item.Subject ?? "Sin título",
                        account,
                        color,
                        due.Year < 4000 ? due : (DateTime?)null,
                        cls != OlTaskClass));
                }
                catch
                {
                }
                finally
                {
                    ReleaseCom(item);
                }
            }
        }
        finally
        {
            ReleaseCom(restricted);
            ReleaseCom(items);
            ReleaseCom(folder);
        }
        return list;
    }

    /// <summary>Marca la tarea (o el mail marcado) como completada en Outlook.</summary>
    public static bool Complete(OutlookTask task)
    {
        var outlook = GetActiveOutlook();
        if (outlook == null)
        {
            return false;
        }

        dynamic? ns = null;
        dynamic? item = null;
        try
        {
            ns = ((dynamic)outlook).GetNamespace("MAPI");
            item = ns.GetItemFromID(task.EntryId, task.StoreId);
            if (task.IsMail)
            {
                item.FlagStatus = OlFlagComplete;
                item.Save();
            }
            else
            {
                item.MarkComplete();
                item.Save();
            }
            return true;
        }
        finally
        {
            ReleaseCom(item);
            ReleaseCom(ns);
            ReleaseCom(outlook);
        }
    }

    /// <summary>Crea una tarea en la carpeta Tareas de la cuenta por defecto de Outlook.</summary>
    public static bool Add(string title)
    {
        var outlook = GetActiveOutlook();
        if (outlook == null)
        {
            return false;
        }

        dynamic? ns = null;
        dynamic? folder = null;
        dynamic? items = null;
        dynamic? item = null;
        try
        {
            ns = ((dynamic)outlook).GetNamespace("MAPI");
            folder = ns.GetDefaultFolder(OlFolderTasks);
            items = folder.Items;
            item = items.Add(OlTaskItem);
            item.Subject = title;
            item.Save();
            return true;
        }
        finally
        {
            ReleaseCom(item);
            ReleaseCom(items);
            ReleaseCom(folder);
            ReleaseCom(ns);
            ReleaseCom(outlook);
        }
    }

    /// <summary>Corre la acción en un hilo STA de fondo (requisito de COM de Outlook).</summary>
    public static Task<T> RunSta<T>(Func<T> action)
    {
        var tcs = new TaskCompletionSource<T>();
        var thread = new Thread(() =>
        {
            try
            {
                tcs.SetResult(action());
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });
        thread.IsBackground = true;
        thread.Name = "WinNotch.OutlookTasks";
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }

    private static void ReleaseCom(object? com)
    {
        try
        {
            if (com != null && Marshal.IsComObject(com))
            {
                Marshal.ReleaseComObject(com);
            }
        }
        catch
        {
        }
    }

    private static object? GetActiveOutlook()
    {
        try
        {
            if (CLSIDFromProgID("Outlook.Application", out var clsid) != 0)
            {
                return null;
            }
            return GetActiveObject(ref clsid, IntPtr.Zero, out var unk) == 0 ? unk : null;
        }
        catch
        {
            return null;
        }
    }

    [DllImport("ole32.dll", CharSet = CharSet.Unicode)]
    private static extern int CLSIDFromProgID(string lpszProgID, out Guid pclsid);

    [DllImport("oleaut32.dll")]
    private static extern int GetActiveObject(ref Guid rclsid, IntPtr pvReserved, [MarshalAs(UnmanagedType.IUnknown)] out object ppunk);
}
