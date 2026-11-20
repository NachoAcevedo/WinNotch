using System.ComponentModel;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows.Media;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media.Imaging;
using WinNotch.Models;
using WinNotch.Services;

namespace PersonalCards;

internal sealed class PublicPosterFallback : IDisposable
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(12) };
    private static readonly Regex Images = new(@"<img\b[^>]*>", RegexOptions.IgnoreCase);
    private static readonly Regex Attributes = new(@"(?<name>alt|src)=""(?<value>[^""]*)""", RegexOptions.IgnoreCase);
    private readonly MediaService _service;
    private readonly Dictionary<string, BitmapImage?> _cache = new();
    private readonly Action<string> _log;
    private CancellationTokenSource? _request;
    private BitmapImage? _poster;
    private bool _disposed;
    private string? _lookupKey;

    public PublicPosterFallback(MediaService service, Action<string> log)
    {
        _service = service;
        _log = log;
        service.MediaInfo.PropertyChanged += OnChanged;
        service.SessionChanged += Refresh;
        Refresh();
    }

    private void OnChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(MediaInfo.AlbumArt) && _poster != null &&
            ReferenceEquals(_service.MediaInfo.AlbumArt, _poster)) return;
        if (args.PropertyName is nameof(MediaInfo.Title) or nameof(MediaInfo.Artist)
            or nameof(MediaInfo.Duration) or nameof(MediaInfo.AlbumArt) or nameof(MediaInfo.HasMedia))
            Refresh();
    }

    private void Refresh()
    {
        if (_disposed) return;
        var media = _service.MediaInfo;
        var session = typeof(MediaService).GetField("_session", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(_service) as Windows.Media.Control.GlobalSystemMediaTransportControlsSession;
        string source = session?.SourceAppUserModelId ?? "";
        // Compare with the app icon so real square album covers retain priority.
        bool candidate = media.HasMedia && (media.AlbumArt == null || IsApplicationLogo(media.AlbumArt, source));
        string title = CleanTitle(media.Title);
        string? key = candidate && title.Length > 1 ? source + ":" + media.Artist + ":" + title : null;
        if (_lookupKey == key)
        {
            if (_poster != null && !ReferenceEquals(media.AlbumArt, _poster))
                media.AlbumArt = _poster;
            return;
        }
        _lookupKey = key;
        _request?.Cancel();
        _request?.Dispose();
        _request = null;
        _poster = null;
        if (key == null) return;
        _request = new CancellationTokenSource();
        _ = FindAsync(key, title, media.Artist, _request.Token);
    }

    internal static string CleanTitle(string title)
    {
        title = Regex.Replace(title, @"\s*[|–—]\s*(?:Netflix|Prime Video|Disney\+|Max|Brave).*$", "",
            RegexOptions.IgnoreCase);
        return Regex.Replace(title.Trim(), @"\s*(?:\((?:19|20)\d{2}\)|(?:19|20)\d{2})\s*$", "").Trim();
    }

    internal static string? FindPoster(string html, string title)
    {
        foreach (Match image in Images.Matches(html))
        {
            var attributes = Attributes.Matches(image.Value).Cast<Match>()
                .ToDictionary(m => m.Groups["name"].Value.ToLowerInvariant(),
                    m => WebUtility.HtmlDecode(m.Groups["value"].Value));
            if (!attributes.TryGetValue("alt", out var alt) ||
                !string.Equals(alt.Trim(), title, StringComparison.OrdinalIgnoreCase) ||
                !attributes.TryGetValue("src", out var src) ||
                !Uri.TryCreate(src, UriKind.Absolute, out var uri) ||
                uri.Scheme != "https" || uri.Host != "media.themoviedb.org")
                continue;
            return src;
        }
        return null;
    }

    private async Task FindAsync(string key, string title, string artist, CancellationToken token)
    {
        try
        {
            await Task.Delay(500, token);
            if (!_cache.TryGetValue(key, out var poster))
            {
                string? url = null;
                if (string.IsNullOrWhiteSpace(artist))
                {
                    string html = await GetPageAsync("https://www.themoviedb.org/search?query=" + Uri.EscapeDataString(title), token);
                    url = FindPoster(html, title);
                }
                if (url == null)
                {
                    string query = title + (string.IsNullOrWhiteSpace(artist) ? "" : " " + artist);
                    string html = await GetPageAsync("https://www.youtube.com/results?search_query=" + Uri.EscapeDataString(query), token);
                    url = FindVideoThumbnail(html, title, artist);
                }
                if (url != null)
                {
                    byte[] bytes = await Http.GetByteArrayAsync(url, token);
                    using var stream = new MemoryStream(bytes);
                    poster = new BitmapImage();
                    poster.BeginInit();
                    poster.CacheOption = BitmapCacheOption.OnLoad;
                    poster.StreamSource = stream;
                    poster.EndInit();
                    poster.Freeze();
                }
                _cache[key] = poster;
            }
            if (_disposed || token.IsCancellationRequested || _lookupKey != key || poster == null) return;
            _poster = poster;
            _service.MediaInfo.AlbumArt = poster;
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is HttpRequestException or IOException or NotSupportedException
            or ArgumentException or InvalidOperationException or JsonException)
        {
            if (!token.IsCancellationRequested)
            {
                _cache[key] = null;
                _log("Media: no se pudo obtener un póster público: " + error.Message);
            }
        }
    }

    private static async Task<string> GetPageAsync(string url, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0 WinNotch/0.7.2");
        using var response = await Http.SendAsync(request, token);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(token);
    }

    internal static string? FindVideoThumbnail(string html, string title, string artist)
    {
        var match = Regex.Match(html, @"ytInitialData\s*=\s*(\{.*?\});</script>", RegexOptions.Singleline);
        if (!match.Success) return null;
        using var data = JsonDocument.Parse(match.Groups[1].Value);
        return FindVideo(data.RootElement, title, artist);
    }

    private static string? FindVideo(JsonElement node, string title, string artist)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            if (node.TryGetProperty("videoRenderer", out var video) &&
                video.TryGetProperty("title", out var name) &&
                video.TryGetProperty("thumbnail", out var image))
            {
                string label = ReadText(name);
                string channel = video.TryGetProperty("longBylineText", out var byline) ? ReadText(byline) : "";
                string Normalize(string text) => Regex.Replace(text.ToLowerInvariant(), @"[^\p{L}\p{N}]+", " ").Trim();
                string normalizedTitle = Normalize(title);
                string normalizedArtist = Normalize(artist);
                if (normalizedTitle.Length > 1 && Normalize(label).Contains(normalizedTitle) &&
                    (normalizedArtist.Length == 0 || Normalize(label + " " + channel).Contains(normalizedArtist)) &&
                    image.TryGetProperty("thumbnails", out var thumbnails))
                {
                    foreach (var thumbnail in thumbnails.EnumerateArray().Reverse())
                    {
                        if (thumbnail.TryGetProperty("url", out var value) &&
                            Uri.TryCreate(value.GetString(), UriKind.Absolute, out var uri) &&
                            uri.Scheme == "https" && uri.Host.EndsWith(".ytimg.com", StringComparison.OrdinalIgnoreCase))
                            return uri.AbsoluteUri;
                    }
                }
            }
            foreach (var property in node.EnumerateObject())
            {
                string? found = FindVideo(property.Value, title, artist);
                if (found != null) return found;
            }
        }
        else if (node.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in node.EnumerateArray())
            {
                string? found = FindVideo(item, title, artist);
                if (found != null) return found;
            }
        }
        return null;
    }

    private static string ReadText(JsonElement node) =>
        node.TryGetProperty("simpleText", out var plain) ? plain.GetString() ?? "" :
        node.TryGetProperty("runs", out var runs)
            ? string.Concat(runs.EnumerateArray().Select(run => run.GetProperty("text").GetString())) : "";

    private readonly Dictionary<string, byte[]?> _appIcons = new(StringComparer.OrdinalIgnoreCase);

    private bool IsApplicationLogo(BitmapImage art, string source)
    {
        if (!_appIcons.TryGetValue(source, out var expected))
        {
            expected = null;
            try
            {
                string processName = Path.GetFileNameWithoutExtension(source);
                foreach (var process in System.Diagnostics.Process.GetProcessesByName(processName))
                {
                    using (process)
                    {
                        string? executable = process.MainModule?.FileName;
                        if (executable == null) continue;
                        using var icon = System.Drawing.Icon.ExtractAssociatedIcon(executable);
                        if (icon == null) continue;
                        var bitmap = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                            icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                        expected = Pixels(bitmap);
                        break;
                    }
                }
            }
            catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException
                or ArgumentException or NotSupportedException) { }
            _appIcons[source] = expected;
        }
        if (expected == null) return false;
        byte[] actual = Pixels(art);
        long difference = 0;
        for (int i = 0; i < actual.Length; i++) difference += Math.Abs(actual[i] - expected[i]);
        return difference / (double)actual.Length < 35;
    }

    private static byte[] Pixels(BitmapSource image)
    {
        var scaled = new TransformedBitmap(image, new ScaleTransform(24.0 / image.PixelWidth, 24.0 / image.PixelHeight));
        var converted = new FormatConvertedBitmap(scaled, PixelFormats.Pbgra32, null, 0);
        var pixels = new byte[24 * 24 * 4];
        converted.CopyPixels(pixels, 24 * 4, 0);
        return pixels;
    }

    public void Dispose()
    {
        _disposed = true;
        _request?.Cancel();
        _request?.Dispose();
        _service.MediaInfo.PropertyChanged -= OnChanged;
        _service.SessionChanged -= Refresh;
    }
}