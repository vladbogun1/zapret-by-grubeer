using System.ComponentModel;
using System.Globalization;

namespace Zapret.Shell;

public sealed record Language(string Tag, string NativeName);

/// <summary>
/// The 2.0 vocabulary. Far smaller than the 1.x table, because the interface says far less: a screen that
/// answers four questions does not need two hundred strings.
/// <para>
/// Bindable as <c>{Binding [key], Source={x:Static local:Text.Current}}</c>; raising the indexer refreshes
/// every bound string, so the language switches with no restart.
/// </para>
/// </summary>
public sealed class Text : INotifyPropertyChanged
{
    public static Text Current { get; } = new();

    private Dictionary<string, string> _table = English;

    private Text() { }

    public event PropertyChangedEventHandler? PropertyChanged;

    public IReadOnlyList<Language> Languages { get; } = [new("ru", "Русский"), new("en", "English")];

    public string Tag { get; private set; } = "en";

    /// <summary>A missing key falls back to English, then to the key itself: visible, never blank, never a crash.</summary>
    public string this[string key] =>
        _table.TryGetValue(key, out var value) ? value
        : English.TryGetValue(key, out var fallback) ? fallback
        : key;

    public string Format(string key, params object?[] arguments) =>
        string.Format(CultureInfo.CurrentCulture, this[key], arguments);

    public void Apply(string? tag)
    {
        var resolved = Languages.FirstOrDefault(l => l.Tag.Equals(tag, StringComparison.OrdinalIgnoreCase))?.Tag
                       ?? Languages.FirstOrDefault(l => l.Tag == CultureInfo.CurrentUICulture.TwoLetterISOLanguageName)?.Tag
                       ?? "en";

        _table = resolved == "ru" ? Russian : English;
        Tag = resolved;

        var culture = CultureInfo.GetCultureInfo(resolved);
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        Changed?.Invoke();
    }

    public event Action? Changed;

    public static readonly Dictionary<string, string> Russian = new(StringComparer.Ordinal)
    {
        ["app"] = "Запрет",
        ["app.by"] = "by Grubeer",

        // onboarding — one question, nothing else
        ["ob.title"] = "Что вы хотите разблокировать?",
        ["ob.subtitle"] = "Отметьте то, чем пользуетесь. Остальное приложение сделает само — можно изменить в любой момент.",
        ["ob.start"] = "Настроить",
        ["ob.skipAll"] = "Пока ничего",

        // stages
        ["stage.preparing"] = "Настраиваем",
        ["stage.working"] = "Всё работает",
        ["stage.workingNoBypass"] = "Обход не нужен",
        ["stage.repairing"] = "Восстанавливаем доступ",
        ["stage.stuck"] = "Не получилось",
        ["stage.off"] = "Защита выключена",
        ["stage.unavailable"] = "Служба недоступна",

        ["sub.working"] = "Интернет свободен. Приложение следит за этим само.",
        ["sub.workingNoBypass"] = "На этой сети ваши сервисы открываются и без обхода. Приложение продолжит следить.",
        ["sub.repairing"] = "Что-то перестало открываться. Подбираем работающий вариант — вмешиваться не нужно.",
        ["sub.off"] = "Ничего не сломано. Включить можно одной кнопкой.",
        ["sub.unavailable"] = "Фоновая служба не отвечает. Без неё приложение не может ни проверить, ни исправить.",

        // the one action per stage
        ["do.turnOn"] = "Включить",
        ["do.turnOff"] = "Выключить",
        ["do.cancel"] = "Отменить",
        ["do.retry"] = "Попробовать снова",
        ["do.details"] = "Подробнее",
        ["do.hideDetails"] = "Скрыть",
        ["do.copyReport"] = "Скопировать отчёт",

        // service verdicts
        ["v.ok"] = "работает",
        ["v.fail"] = "не открывается",
        ["v.checking"] = "проверяем",
        ["speed.fast"] = "быстро",
        ["speed.normal"] = "нормально",
        ["speed.slow"] = "медленно",

        // steps
        ["step.checkingWithoutBypass"] = "Проверяем без обхода",
        ["step.noBypassNeeded"] = "Обход не требуется",
        ["step.trying"] = "Пробуем вариант {0}",
        ["step.fixed"] = "Готово — всё открывается",
        ["step.exhausted"] = "Перебрали вариантов: {0}",
        ["step.noEngine"] = "Движок не установлен",
        ["step.noCandidates"] = "Нет доступных вариантов",

        // advice — always something a person can do
        ["advice.tryGameFilter"] = "Похоже, блокируется что-то ещё. Попробуйте включить поддержку игр и голосового чата.",
        ["advice.widenIpSet"] = "Сейчас обход ограничен списком адресов. Снимите ограничение и попробуйте снова.",
        ["advice.sendReport"] = "Ваш провайдер, похоже, блокирует иначе. Скопируйте отчёт и покажите его — по нему видно, что уже пробовали.",
        ["advice.tryFullSweep"] = "Можно проверить все варианты подряд — это занимает несколько минут.",
        ["advice.installEngine"] = "Сначала нужно установить движок обхода.",

        // details
        ["d.strategy"] = "Вариант обхода",
        ["d.engine"] = "Движок",
        ["d.uptime"] = "Работает",
        ["d.network"] = "Сеть",
        ["d.none"] = "нет",
        ["d.language"] = "Язык",

        // updates — the only decision the product asks the user to make
        ["up.check"] = "Проверить обновления",
        ["up.checking"] = "Проверяем обновления…",
        ["up.current"] = "Версия {0} — актуальная. Движок {1}.",
        ["up.available"] = "Доступна версия {0}. Обновление устанавливается обычным установщиком, ваши настройки сохранятся.",
        ["up.engineAvailable"] = "Доступна новая версия движка: {0}. Обновление проходит с откатом, если что-то пойдёт не так.",
        ["up.update"] = "Обновить",
        ["up.offline"] = "Не удалось проверить обновления. Это не мешает работе.",
        ["up.started"] = "Установщик запущен.",

        // tray
        ["tray.open"] = "Открыть",
        ["tray.exit"] = "Выход",
    };

    public static readonly Dictionary<string, string> English = new(StringComparer.Ordinal)
    {
        ["app"] = "Запрет",
        ["app.by"] = "by Grubeer",

        ["ob.title"] = "What do you want unblocked?",
        ["ob.subtitle"] = "Tick what you use. The app handles the rest — you can change this any time.",
        ["ob.start"] = "Set up",
        ["ob.skipAll"] = "Nothing for now",

        ["stage.preparing"] = "Setting up",
        ["stage.working"] = "Everything works",
        ["stage.workingNoBypass"] = "No bypass needed",
        ["stage.repairing"] = "Restoring access",
        ["stage.stuck"] = "That did not work",
        ["stage.off"] = "Protection is off",
        ["stage.unavailable"] = "Service unavailable",

        ["sub.working"] = "Your internet is unrestricted. The app watches this on its own.",
        ["sub.workingNoBypass"] = "On this connection your services open without a bypass. The app keeps watching.",
        ["sub.repairing"] = "Something stopped opening. Finding a working option — nothing for you to do.",
        ["sub.off"] = "Nothing is broken. One click turns it back on.",
        ["sub.unavailable"] = "The background service is not responding. Without it the app can neither check nor fix.",

        ["do.turnOn"] = "Turn on",
        ["do.turnOff"] = "Turn off",
        ["do.cancel"] = "Cancel",
        ["do.retry"] = "Try again",
        ["do.details"] = "Details",
        ["do.hideDetails"] = "Hide",
        ["do.copyReport"] = "Copy report",

        ["v.ok"] = "works",
        ["v.fail"] = "not opening",
        ["v.checking"] = "checking",
        ["speed.fast"] = "fast",
        ["speed.normal"] = "normal",
        ["speed.slow"] = "slow",

        ["step.checkingWithoutBypass"] = "Checking without a bypass",
        ["step.noBypassNeeded"] = "No bypass needed",
        ["step.trying"] = "Trying option {0}",
        ["step.fixed"] = "Done — everything opens",
        ["step.exhausted"] = "Options tried: {0}",
        ["step.noEngine"] = "The engine is not installed",
        ["step.noCandidates"] = "No options available",

        ["advice.tryGameFilter"] = "Something else seems to be blocked. Try enabling support for games and voice chat.",
        ["advice.widenIpSet"] = "The bypass is currently limited to a list of addresses. Remove the limit and try again.",
        ["advice.sendReport"] = "Your provider appears to block differently. Copy the report and share it — it shows what was already tried.",
        ["advice.tryFullSweep"] = "You can check every option in turn; that takes a few minutes.",
        ["advice.installEngine"] = "The bypass engine needs to be installed first.",

        ["d.strategy"] = "Bypass option",
        ["d.engine"] = "Engine",
        ["d.uptime"] = "Running for",
        ["d.network"] = "Network",
        ["d.none"] = "none",
        ["d.language"] = "Language",

        ["up.check"] = "Check for updates",
        ["up.checking"] = "Checking for updates…",
        ["up.current"] = "Version {0} is up to date. Engine {1}.",
        ["up.available"] = "Version {0} is available. It installs with the usual installer and keeps your settings.",
        ["up.engineAvailable"] = "A new engine version is available: {0}. The update rolls back if anything goes wrong.",
        ["up.update"] = "Update",
        ["up.offline"] = "Could not check for updates. This does not affect anything.",
        ["up.started"] = "The installer has been started.",

        ["tray.open"] = "Open",
        ["tray.exit"] = "Exit",
    };
}
