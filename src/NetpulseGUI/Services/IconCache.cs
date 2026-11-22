using System.Collections.Concurrent;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace NetpulseGUI.Services;

/// <summary>Кэш иконок exe. Извлечение — в фоне, результат заморожен и потокобезопасен.</summary>
public sealed class IconCache
{
    private readonly ConcurrentDictionary<string, ImageSource?> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Task<ImageSource?>> _pending = new(StringComparer.OrdinalIgnoreCase);

    public ImageSource? TryGet(string path) => _cache.TryGetValue(path, out var img) ? img : null;

    public Task<ImageSource?> GetAsync(string path)
    {
        if (string.IsNullOrEmpty(path)) return Task.FromResult<ImageSource?>(null);
        if (_cache.TryGetValue(path, out var cached)) return Task.FromResult(cached);
        return _pending.GetOrAdd(path, p => Task.Run(() =>
        {
            var img = Extract(p);
            _cache[p] = img;
            _pending.TryRemove(p, out _);
            return img;
        }));
    }

    private static ImageSource? Extract(string path)
    {
        try
        {
            if (!Path.IsPathRooted(path) || !File.Exists(path)) return null;
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
            if (icon == null) return null;
            var src = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
