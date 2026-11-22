using System.ComponentModel;
using System.Globalization;
using System.Resources;
using System.Windows.Data;
using System.Windows.Markup;
using NetpulseGUI.Core.Formatting;

namespace NetpulseGUI.Localization;

/// <summary>
/// Локализация на .resx (Strings.resx — русский по умолчанию, Strings.en.resx — английский)
/// с переключением языка «на лету»: XAML привязывается к индексатору, который уведомляет об изменении.
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    private static readonly ResourceManager Resources = new("NetpulseGUI.Resources.Strings", typeof(Loc).Assembly);

    public static Loc Instance { get; } = new();

    private CultureInfo _culture = CultureInfo.GetCultureInfo("ru-RU");

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Язык сменился (после обновления привязок).</summary>
    public event Action? LanguageChanged;

    public CultureInfo Culture => _culture;

    public string this[string key] => Resources.GetString(key, _culture) ?? $"[{key}]";

    public void SetLanguage(string language)
    {
        _culture = language == "en" ? CultureInfo.GetCultureInfo("en-US") : CultureInfo.GetCultureInfo("ru-RU");
        UnitFormatter.UseRussian = language != "en";
        CultureInfo.DefaultThreadCurrentUICulture = _culture;
        CultureInfo.DefaultThreadCurrentCulture = _culture;
        Thread.CurrentThread.CurrentUICulture = _culture;
        Thread.CurrentThread.CurrentCulture = _culture;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        LanguageChanged?.Invoke();
    }

    public static string T(string key) => Instance[key];

    public static string F(string key, params object?[] args) => string.Format(Instance._culture, Instance[key], args);
}

/// <summary>Разметка: <c>Text="{l:Loc Nav_Dashboard}"</c>.</summary>
[MarkupExtensionReturnType(typeof(BindingExpression))]
public sealed class LocExtension : MarkupExtension
{
    public LocExtension() { }

    public LocExtension(string key) => Key = key;

    [ConstructorArgument("key")]
    public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new Binding($"[{Key}]") { Source = Loc.Instance, Mode = BindingMode.OneWay };
        return binding.ProvideValue(serviceProvider);
    }
}
