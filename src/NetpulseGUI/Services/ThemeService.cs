using System.Windows;
using System.Windows.Media;
using NetpulseGUI.Settings;
using SkiaSharp;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace NetpulseGUI.Services;

/// <summary>Тема приложения (система/светлая/тёмная), Mica и системный акцентный цвет; палитра для графиков.</summary>
public sealed class ThemeService
{
    private Window? _watched;

    public event Action? ThemeChanged;

    public bool IsDark => ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Dark;

    public ThemeService()
    {
        ApplicationThemeManager.Changed += (_, _) => ThemeChanged?.Invoke();
    }

    public void Apply(ThemePreference mode, Window? mainWindow)
    {
        if (_watched != null)
        {
            try { SystemThemeWatcher.UnWatch(_watched); } catch (Exception) { }
            _watched = null;
        }

        switch (mode)
        {
            case ThemePreference.Light:
                ApplicationThemeManager.Apply(ApplicationTheme.Light, WindowBackdropType.Mica, true);
                break;
            case ThemePreference.Dark:
                ApplicationThemeManager.Apply(ApplicationTheme.Dark, WindowBackdropType.Mica, true);
                break;
            default:
                ApplicationThemeManager.ApplySystemTheme(true);
                if (mainWindow != null)
                {
                    SystemThemeWatcher.Watch(mainWindow, WindowBackdropType.Mica, true);
                    _watched = mainWindow;
                }
                break;
        }
        ThemeChanged?.Invoke();
    }

    // ---- Палитра графиков ----

    public SKColor DownColor => ToSk(AccentColor());
    public SKColor UpColor => IsDark ? new SKColor(255, 170, 80) : new SKColor(214, 105, 0);
    public SKColor LabelColor => IsDark ? new SKColor(200, 200, 200) : new SKColor(90, 90, 90);
    public SKColor SeparatorColor => IsDark ? new SKColor(255, 255, 255, 22) : new SKColor(0, 0, 0, 22);

    public static readonly SKColor[] PiePalette =
    {
        new(0, 120, 212), new(0, 183, 195), new(142, 140, 216), new(255, 140, 0), new(232, 17, 35),
        new(16, 124, 16), new(194, 57, 179), new(255, 185, 0), new(0, 153, 188), new(122, 117, 116)
    };

    private static Color AccentColor()
    {
        try
        {
            if (Application.Current?.TryFindResource("SystemAccentColorPrimary") is Color c) return c;
            return ApplicationAccentColorManager.PrimaryAccent;
        }
        catch (Exception)
        {
            return Color.FromRgb(0, 120, 212);
        }
    }

    private static SKColor ToSk(Color c) => new(c.R, c.G, c.B, c.A);
}
