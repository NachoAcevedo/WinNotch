using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using WinNotch.Models;

namespace WinNotch.Services;

/// <summary>
/// Provides calendar events. Currently reads from Outlook COM if available,
/// otherwise returns an empty list. Can be extended for Microsoft Graph, .ics files, etc.
/// </summary>
public class CalendarService : IDisposable
{
    public event Action? EventsUpdated;

    public List<CalendarEvent> TodayEvents { get; private set; } = new();

    private System.Windows.Threading.DispatcherTimer? _refreshTimer;
    private int _refreshing;

    public void Initialize()
    {
        RefreshEvents();

        // Refresh every 5 minutes
        _refreshTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMinutes(5)
        };
        _refreshTimer.Tick += (_, _) => RefreshEvents();
        _refreshTimer.Start();
    }

    public void RefreshEvents()
    {
        // COM interop can take seconds — never block the UI thread, never overlap.
        if (Interlocked.CompareExchange(ref _refreshing, 1, 0) != 0) return;

        // Outlook COM prefers an STA apartment; thread-pool threads are MTA.
        var thread = new Thread(() =>
        {
            try
            {
                var events = FetchOutlookEvents();
                TodayEvents = events;
                EventsUpdated?.Invoke();
            }
            finally
            {
                Interlocked.Exchange(ref _refreshing, 0);
            }
        })
        {
            IsBackground = true,
            Name = "WinNotch.CalendarRefresh"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    public const int MaxEvents = 5;

    // Join links of the common meeting providers, as they appear in the invite body/location.
    private static readonly Regex JoinUrlRegex = new(
        @"https://(?:teams\.microsoft\.com/(?:l/meetup-join|meet)/|teams\.live\.com/meet/|[\w.-]*zoom\.us/(?:j|my|w)/|meet\.google\.com/|[\w.-]*webex\.com/(?:meet|join|[\w.-]+/j\.php))[^\s<>""')\]]+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static List<CalendarEvent> FetchOutlookEvents()
    {
        object? outlook = null;
        object? ns = null;
        object? stores = null;

        try
        {
            // Attach to a RUNNING Outlook instance only. CreateInstance would
            // silently launch Outlook in the background on every refresh.
            outlook = GetActiveOutlook();
            if (outlook == null) return new List<CalendarEvent>();

            dynamic ol = outlook;
            ns = ol.GetNamespace("MAPI");
            dynamic dns = ns!;
            stores = dns.Stores;

            var events = new List<CalendarEvent>();
            // Every account configured in Outlook, not just the default one.
            foreach (dynamic store in (dynamic)stores!)
            {
                try { events.AddRange(FetchStoreEvents(store)); }
                catch { }
                finally { ReleaseCom(store); }
            }

            return events
                .OrderBy(e => e.Start)
                .Take(MaxEvents)
                .ToList();
        }
        catch
        {
            return new List<CalendarEvent>();
        }
        finally
        {
            ReleaseCom(stores);
            ReleaseCom(ns);
            ReleaseCom(outlook);
        }
    }

    private static List<CalendarEvent> FetchStoreEvents(dynamic store)
    {
        object? calFolder = null;
        object? items = null;
        object? restricted = null;
        var events = new List<CalendarEvent>();

        try
        {
            // olFolderCalendar = 9 (throws for stores without a calendar, e.g. archives)
            calFolder = store.GetDefaultFolder(9);
            dynamic dcal = calFolder!;
            items = dcal.Items;
            dynamic ditems = items!;
            ditems.Sort("[Start]");
            ditems.IncludeRecurrences = true;

            var now = DateTime.Now;
            var endOfDay = now.Date.AddDays(1).AddSeconds(-1);

            // Today's events that haven't ended yet: a meeting stays listed
            // (and joinable) until its end time. Outlook expects locale-formatted dates.
            string filter = $"[End] > '{now:g}' AND [Start] <= '{endOfDay:g}'";
            restricted = ditems.Restrict(filter);

            foreach (dynamic item in (dynamic)restricted!)
            {
                try
                {
                    events.Add(new CalendarEvent
                    {
                        Title = item.Subject ?? "Untitled",
                        Start = item.Start,
                        End = item.End,
                        IsAllDay = item.AllDayEvent,
                        CalendarColor = "#4A90D9",
                        JoinUrl = ExtractJoinUrl(item)
                    });
                }
                catch { }
                finally { ReleaseCom(item); }
                if (events.Count >= MaxEvents) break; // sorted by start: the rest come later
            }
        }
        finally
        {
            ReleaseCom(restricted);
            ReleaseCom(items);
            ReleaseCom(calFolder);
        }
        return events;
    }

    private static string? ExtractJoinUrl(dynamic item)
    {
        string location = "", body = "";
        try { location = item.Location ?? ""; } catch { }
        try { body = item.Body ?? ""; } catch { }

        var match = JoinUrlRegex.Match(location);
        if (!match.Success) match = JoinUrlRegex.Match(body);
        return match.Success ? match.Value.TrimEnd('.', ',', ';', '>') : null;
    }

    private static void ReleaseCom(object? com)
    {
        try
        {
            if (com != null && Marshal.IsComObject(com))
                Marshal.ReleaseComObject(com);
        }
        catch { }
    }

    /// <summary>
    /// Equivalent of Marshal.GetActiveObject (removed in .NET Core+):
    /// returns the running Outlook.Application or null if Outlook isn't open.
    /// </summary>
    private static object? GetActiveOutlook()
    {
        try
        {
            if (CLSIDFromProgID("Outlook.Application", out var clsid) != 0)
                return null;

            return GetActiveObject(ref clsid, IntPtr.Zero, out var obj) == 0 ? obj : null;
        }
        catch
        {
            return null;
        }
    }

    [DllImport("ole32.dll", CharSet = CharSet.Unicode)]
    private static extern int CLSIDFromProgID(string lpszProgID, out Guid pclsid);

    [DllImport("oleaut32.dll")]
    private static extern int GetActiveObject(ref Guid rclsid, IntPtr pvReserved,
        [MarshalAs(UnmanagedType.IUnknown)] out object ppunk);

    public void Dispose()
    {
        _refreshTimer?.Stop();
    }
}
