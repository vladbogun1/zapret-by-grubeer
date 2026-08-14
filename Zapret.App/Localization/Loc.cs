using System.ComponentModel;
using System.Globalization;

namespace Zapret.App.Localization;

public sealed record LanguageOption(string Tag, string NativeName);

/// <summary>
/// The application's translation table, bindable from XAML as
/// <c>{Binding [some.key], Source={x:Static loc:Loc.Instance}}</c>.
/// <para>
/// Raising <c>PropertyChanged</c> for the indexer refreshes every bound string at once, so the language
/// switches live with no restart and no reloaded windows. Translations live in code rather than satellite
/// assemblies deliberately: a missing resource file would degrade the whole UI to keys at runtime, while a
/// missing key here is caught by the fallback below and stays visible in one place.
/// </para>
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    public const string FallbackTag = "en";

    public static Loc Instance { get; } = new();

    private Dictionary<string, string> _current = Translations.English;
    private Dictionary<string, string> _fallback = Translations.English;

    private Loc() { }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Every language the application ships. Adding one means adding a table and a row here.</summary>
    public IReadOnlyList<LanguageOption> Languages { get; } =
    [
        new("ru", "Русский"),
        new("en", "English"),
    ];

    public string CurrentTag { get; private set; } = FallbackTag;

    /// <summary>
    /// A missing key returns the fallback language, and failing that the key itself — visible, but never a
    /// crash and never an empty label.
    /// </summary>
    public string this[string key] =>
        _current.TryGetValue(key, out var value) ? value
        : _fallback.TryGetValue(key, out var fallback) ? fallback
        : key;

    public string Format(string key, params object?[] arguments) =>
        string.Format(CultureInfo.CurrentCulture, this[key], arguments);

    /// <summary>
    /// Applies a language. <paramref name="tag"/> null follows Windows; an unsupported system language
    /// falls back rather than showing keys.
    /// </summary>
    public void Apply(string? tag)
    {
        var resolved = Resolve(tag);

        _current = TableFor(resolved);
        CurrentTag = resolved;

        // Culture affects number and time formatting in bindings, so it follows the chosen language.
        var culture = CultureInfo.GetCultureInfo(resolved);
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentTag)));
        LanguageChanged?.Invoke();
    }

    /// <summary>Raised after a language change, for anything that formats strings in code.</summary>
    public event Action? LanguageChanged;

    private string Resolve(string? tag)
    {
        if (!string.IsNullOrWhiteSpace(tag))
        {
            var exact = Languages.FirstOrDefault(l => l.Tag.Equals(tag, StringComparison.OrdinalIgnoreCase));
            if (exact is not null) return exact.Tag;
        }

        // Follow Windows: match the two-letter language, ignoring the region.
        var system = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        var matched = Languages.FirstOrDefault(l => l.Tag.Equals(system, StringComparison.OrdinalIgnoreCase));

        return matched?.Tag ?? FallbackTag;
    }

    private static Dictionary<string, string> TableFor(string tag) => tag switch
    {
        "ru" => Translations.Russian,
        _ => Translations.English,
    };
}
