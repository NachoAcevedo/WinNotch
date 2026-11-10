using System;
using System.Collections.ObjectModel;
using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WinNotch.Models;

namespace WinNotch.Services;

/// <summary>
/// Manages the file shelf — temporary file staging area on the notch.
/// </summary>
public class ShelfService
{
    private readonly string _statePath;
    public ShelfService(string? statePath = null)
    {
        _statePath = statePath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WinNotch", "shelf.json");
        try
        {
            if (File.Exists(_statePath)) AddFiles(System.Text.Json.JsonSerializer.Deserialize<string[]>(File.ReadAllText(_statePath)) ?? Array.Empty<string>());
        }
        catch { }
    }

    private void SaveReferences()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
            var temporary = _statePath + ".tmp";
            File.WriteAllText(temporary, System.Text.Json.JsonSerializer.Serialize(Items.Select(item => item.FilePath).ToArray()));
            File.Move(temporary, _statePath, true);
        }
        catch (Exception ex) { System.Diagnostics.Trace.TraceWarning($"Shelf state: {ex.Message}"); }
    }
    public ObservableCollection<ShelfItem> Items { get; } = new();

    public event Action? ShelfChanged;

    public bool HasItems => Items.Count > 0;

    public void AddFiles(string[] paths)
    {
        foreach (var path in paths)
        {
            if (!File.Exists(path) && !Directory.Exists(path)) continue;
            // Avoid duplicates
            bool exists = false;
            foreach (var item in Items)
            {
                if (string.Equals(item.FilePath, path, StringComparison.OrdinalIgnoreCase))
                {
                    exists = true;
                    break;
                }
            }
            if (exists) continue;

            bool isFolder = Directory.Exists(path);
            var name = isFolder ? new DirectoryInfo(path).Name : Path.GetFileName(path);
            var ext = isFolder ? "" : Path.GetExtension(path);

            var item2 = new ShelfItem
            {
                FilePath = path,
                FileName = name,
                Extension = ext,
                IsFolder = isFolder,
                Icon = GetFileIcon(path, isFolder),
                AddedAt = DateTime.Now
            };

            Items.Add(item2);
        }

        SaveReferences();
        ShelfChanged?.Invoke();
    }

    public void Remove(ShelfItem item)
    {
        Items.Remove(item);
        SaveReferences();
        ShelfChanged?.Invoke();
    }

    public void Clear()
    {
        Items.Clear();
        SaveReferences();
        ShelfChanged?.Invoke();
    }

    /// <summary>
    /// Extract the system file icon as an ImageSource.
    /// </summary>
    private static ImageSource? GetFileIcon(string path, bool isFolder)
    {
        try
        {
            Icon? icon;
            if (isFolder)
            {
                // Use shell to get folder icon
                var shInfo = new NativeMethods.SHFILEINFO();
                NativeMethods.SHGetFileInfo(path, 0, ref shInfo,
                    (uint)System.Runtime.InteropServices.Marshal.SizeOf(shInfo),
                    NativeMethods.SHGFI_ICON | NativeMethods.SHGFI_LARGEICON);
                if (shInfo.hIcon == IntPtr.Zero) return null;
                icon = System.Drawing.Icon.FromHandle(shInfo.hIcon);
            }
            else
            {
                // Use shell for file icon too (gets the associated app icon)
                var shInfo = new NativeMethods.SHFILEINFO();
                NativeMethods.SHGetFileInfo(path, 0, ref shInfo,
                    (uint)System.Runtime.InteropServices.Marshal.SizeOf(shInfo),
                    NativeMethods.SHGFI_ICON | NativeMethods.SHGFI_LARGEICON);
                if (shInfo.hIcon == IntPtr.Zero) return null;
                icon = System.Drawing.Icon.FromHandle(shInfo.hIcon);
            }

            var bitmapSource = Imaging.CreateBitmapSourceFromHIcon(
                icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            bitmapSource.Freeze();

            NativeMethods.DestroyIcon(icon.Handle);
            return bitmapSource;
        }
        catch
        {
            return null;
        }
    }
}

internal static class NativeMethods
{
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Auto)]
    public struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    public const uint SHGFI_ICON = 0x000000100;
    public const uint SHGFI_LARGEICON = 0x000000000;

    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
    public static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes,
        ref SHFILEINFO psfi, uint cbSizeFileInfo, uint uFlags);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    public static extern bool DestroyIcon(IntPtr hIcon);
}
