using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace PersonalCards;

/// <summary>
/// Abre Outlook clásico sin ventana (solo el ícono en la bandeja), con el perfil y la sesión de
/// siempre, y lo mantiene vivo mientras WinNotch esté abierto: arrancado por automatización,
/// Outlook se cierra solo cuando nadie lo usa. Si después se abre la ventana de Outlook, usa
/// este mismo proceso.
/// </summary>
internal static class OutlookKeeper
{
    private static readonly object Gate = new();
    private static Thread? _thread;
    private static Dispatcher? _dispatcher;

    public static void Start(Action<string> log)
    {
        lock (Gate)
        {
            if (_thread is { IsAlive: true })
            {
                return;
            }

            // Hilo STA propio que crea Outlook y conserva la referencia: mientras exista,
            // Outlook sigue abierto y los demás lo encuentran con GetActiveObject.
            _thread = new Thread(() =>
            {
                object? app = null;
                try
                {
                    var type = Type.GetTypeFromProgID("Outlook.Application");
                    if (type == null)
                    {
                        log("Outlook clásico no está instalado");
                        return;
                    }
                    app = Activator.CreateInstance(type);
                    // Logon() sin argumentos muestra el diálogo "Choose Profile": se entra directo
                    // con el perfil predeterminado, sin diálogo y reutilizando la sesión si ya hay una.
                    object profile = DefaultProfile() ?? Type.Missing;
                    ((dynamic)app!).GetNamespace("MAPI").Logon(profile, Type.Missing, false, false);
                    log("Outlook abierto en la bandeja");
                    _dispatcher = Dispatcher.CurrentDispatcher;
                    Dispatcher.Run();
                }
                catch (Exception ex)
                {
                    log("no se pudo abrir Outlook en la bandeja: " + ex.Message);
                }
                finally
                {
                    _dispatcher = null;
                    if (app != null && Marshal.IsComObject(app))
                    {
                        Marshal.ReleaseComObject(app);
                    }
                }
            })
            {
                IsBackground = true,
                Name = "WinNotch.OutlookKeeper"
            };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
        }
    }

    /// <summary>Perfil predeterminado de Outlook (el que usa al abrirse normalmente).</summary>
    private static string? DefaultProfile()
    {
        foreach (var version in new[] { "16.0", "15.0" })
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey($@"Software\Microsoft\Office\{version}\Outlook");
            if (key?.GetValue("DefaultProfile") is string name && name.Length > 0) return name;
        }
        return null;
    }

    /// <summary>Suelta la referencia: si Outlook no tiene ventana abierta, se cierra solo.</summary>
    public static void Stop()
    {
        _dispatcher?.InvokeShutdown();
    }
}
