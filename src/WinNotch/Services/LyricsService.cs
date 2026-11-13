using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using WinNotch.Helpers;
using WinNotch.Models;

namespace WinNotch.Services;

/// <summary>
/// Letra sincronizada de la canción actual, desde LRCLIB (https://lrclib.net: gratis, sin clave).
/// Solo consulta cuando está habilitada; guarda en memoria el resultado de cada canción
/// (también "sin letra") para no repetir pedidos. <see cref="CurrentLine"/> sigue la
/// posición de reproducción; queda vacía cuando la canción no tiene letra sincronizada.
/// </summary>
public sealed class LyricsService : IDisposable
{
    private sealed record LyricLine(TimeSpan Time, string Text);

    private static readonly HttpClient Http = CreateClient();
    private static readonly TimeSpan LyricLead = TimeSpan.FromSeconds(0.75);
    private static readonly Regex TimeTag = new(@"\[(\d+):(\d{1,2}(?:[.:]\d{1,3})?)\]", RegexOptions.Compiled);

    private readonly Dictionary<string, LyricLine[]?> _cache = new();
    private MediaInfo? _info;
    private LyricLine[]? _lines;
    private string? _trackKey;
    private CancellationTokenSource? _fetch;
    private bool _enabled;

    /// <summary>Línea que se está cantando ahora; vacía si no hay letra sincronizada.</summary>
    public string CurrentLine { get; private set; } = string.Empty;

    /// <summary>La canción actual tiene letra sincronizada.</summary>
    public bool HasLyrics => _lines is { Length: > 0 };

    /// <summary>Se está buscando la letra de la canción actual (todavía no se sabe si tiene).</summary>
    public bool IsLoading { get; private set; }

    /// <summary>Cambió <see cref="CurrentLine"/> o <see cref="HasLyrics"/>. Siempre en el hilo de la UI.</summary>
    public event Action? Changed;

    public void Bind(MediaInfo info)
    {
        _info = info;
        info.PropertyChanged += (_, e) =>
        {
            if (!_enabled) return;
            if (e.PropertyName is nameof(MediaInfo.Title) or nameof(MediaInfo.Artist) or nameof(MediaInfo.Duration))
                OnUi(LoadForCurrentTrack);
            else if (e.PropertyName == nameof(MediaInfo.Position))
                OnUi(() => UpdateCurrentLine());
        };
    }

    public void SetEnabled(bool enabled)
    {
        if (_enabled == enabled) return;
        _enabled = enabled;
        if (enabled)
        {
            LoadForCurrentTrack();
        }
        else
        {
            _fetch?.Cancel();
            _trackKey = null;
            SetLoading(false);
            SetLines(null);
        }
    }

    private void LoadForCurrentTrack()
    {
        var info = _info;
        if (info == null || !info.HasMedia || string.IsNullOrWhiteSpace(info.Title))
        {
            _trackKey = null;
            SetLoading(false);
            SetLines(null);
            return;
        }

        string key = $"{info.Artist}|{info.Title}".ToLowerInvariant();
        if (key == _trackKey) return;

        if (_cache.TryGetValue(key, out var cached))
        {
            _trackKey = key;
            SetLoading(false);
            SetLines(cached);
            return;
        }

        // Canción nueva: mientras se busca no se muestra nada (ni la letra anterior ni el título).
        SetLoading(true);
        SetLines(null);

        // La duración puede llegar después del título: se espera a tenerla para pedir la letra justa.
        if (info.Duration <= TimeSpan.Zero) return;

        _trackKey = key;
        _fetch?.Cancel();
        var cts = _fetch = new CancellationTokenSource();
        _ = FetchAsync(key, info.Title, info.Artist, info.AlbumTitle, info.Duration, cts.Token);
    }

    private async Task FetchAsync(string key, string title, string artist, string album, TimeSpan duration, CancellationToken token)
    {
        LyricLine[]? lines = null;
        bool failed = false;
        try
        {
            lines = await QueryAsync(title, artist, album, duration, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException)
        {
            failed = true;
            System.Diagnostics.Debug.WriteLine($"Lyrics fetch failed: {ex.Message}");
        }

        OnUi(() =>
        {
            if (token.IsCancellationRequested) return;
            // Un error de red no se guarda: se vuelve a intentar la próxima vez que suene.
            if (!failed) _cache[key] = lines;
            if (key != _trackKey) return;
            SetLoading(false);
            SetLines(lines);
        });
    }

    private void SetLoading(bool loading)
    {
        if (IsLoading == loading) return;
        IsLoading = loading;
        Changed?.Invoke();
    }

    private static async Task<LyricLine[]?> QueryAsync(string title, string artist, string album, TimeSpan duration, CancellationToken token)
    {
        var exact = "https://lrclib.net/api/get?" + Query(
            ("track_name", title), ("artist_name", artist), ("album_name", album),
            ("duration", ((int)Math.Round(duration.TotalSeconds)).ToString(CultureInfo.InvariantCulture)));

        using (var response = await Http.GetAsync(exact, token))
        {
            if (response.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
                var lines = Parse(doc.RootElement);
                if (lines != null) return lines;
            }
        }

        // Sin coincidencia exacta (otro álbum, duración algo distinta): búsqueda por título y artista,
        // quedándose con la letra sincronizada de duración más parecida.
        var search = "https://lrclib.net/api/search?" + Query(("track_name", title), ("artist_name", artist));
        using var results = await Http.GetAsync(search, token);
        if (!results.IsSuccessStatusCode) return null;

        using var list = JsonDocument.Parse(await results.Content.ReadAsStringAsync(token));
        if (list.RootElement.ValueKind != JsonValueKind.Array) return null;

        return list.RootElement.EnumerateArray()
            .Where(item => item.TryGetProperty("syncedLyrics", out var s) && s.ValueKind == JsonValueKind.String)
            .Select(item => (item, gap: item.TryGetProperty("duration", out var d) && d.TryGetDouble(out var secs)
                ? Math.Abs(secs - duration.TotalSeconds) : double.MaxValue))
            .Where(x => x.gap <= 5)
            .OrderBy(x => x.gap)
            .Select(x => Parse(x.item))
            .FirstOrDefault(lines => lines != null);
    }

    private static LyricLine[]? Parse(JsonElement item)
    {
        if (!item.TryGetProperty("syncedLyrics", out var synced) || synced.ValueKind != JsonValueKind.String)
            return null;

        var lines = new List<LyricLine>();
        foreach (var raw in (synced.GetString() ?? "").Split('\n'))
        {
            var matches = TimeTag.Matches(raw);
            if (matches.Count == 0) continue;
            string text = TimeTag.Replace(raw, "").Trim();
            foreach (Match m in matches)
            {
                int minutes = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                double seconds = double.Parse(m.Groups[2].Value.Replace(':', '.'), CultureInfo.InvariantCulture);
                lines.Add(new LyricLine(TimeSpan.FromMinutes(minutes) + TimeSpan.FromSeconds(seconds), text));
            }
        }
        return lines.Count == 0 ? null : lines.OrderBy(l => l.Time).ToArray();
    }

    private void SetLines(LyricLine[]? lines)
    {
        bool hadLyrics = HasLyrics;
        _lines = lines;
        if (!UpdateCurrentLine() && hadLyrics != HasLyrics)
            Changed?.Invoke();
    }

    /// <summary>Devuelve true si avisó un cambio.</summary>
    private bool UpdateCurrentLine()
    {
        string line = string.Empty;
        if (_lines is { Length: > 0 } lines && _info != null)
        {
            // Adelanto: entre que el reproductor informa la posición y la línea se ve pasa
            // un momento; sin esto la letra aparece tarde respecto de lo que se escucha.
            var position = _info.Position + LyricLead;
            int index = -1;
            for (int lo = 0, hi = lines.Length - 1; lo <= hi;)
            {
                int mid = (lo + hi) / 2;
                if (lines[mid].Time <= position) { index = mid; lo = mid + 1; }
                else hi = mid - 1;
            }
            // Antes de la primera línea o en un silencio instrumental: nota musical.
            line = index < 0 || lines[index].Text.Length == 0 ? "♪ instrumental ♪" : lines[index].Text;
        }

        if (line == CurrentLine) return false;
        CurrentLine = line;
        Changed?.Invoke();
        return true;
    }

    private static string Query(params (string Name, string Value)[] parts) =>
        string.Join("&", parts.Where(p => !string.IsNullOrWhiteSpace(p.Value))
            .Select(p => $"{p.Name}={Uri.EscapeDataString(p.Value)}"));

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        // LRCLIB pide identificar la app; no se envía ningún dato del usuario.
        if (!client.DefaultRequestHeaders.UserAgent.TryParseAdd($"WinNotch/{AppInfo.Version}"))
            client.DefaultRequestHeaders.UserAgent.TryParseAdd("WinNotch");
        return client;
    }

    private static void OnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(action);
    }

    public void Dispose() => _fetch?.Cancel();
}
