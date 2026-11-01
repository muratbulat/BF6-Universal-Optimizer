using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Windows.Data;
using System.Windows.Markup;
using BF6Optimizer.Core.Common;

namespace BF6Optimizer.App.Localization;

/// <summary>Embedded tr/en string tables, switchable at runtime. Missing keys show the key, never crash.</summary>
public sealed class Loc : INotifyPropertyChanged
{
    public static Loc Instance { get; } = new();

    private Dictionary<string, string> _strings = [];

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Language { get; private set; } = "tr";

    private Loc() => SetLanguage("tr");

    public string this[string key] => _strings.GetValueOrDefault(key, key);

    public void SetLanguage(string language)
    {
        Language = language == "en" ? "en" : "tr";
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(Language + ".json")!;
        _strings = JsonSerializer.Deserialize<Dictionary<string, string>>(s)!;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
    }

    public string Format(string key, params object[] args)
    {
        var text = this[key];
        return args.Length == 0 ? text : string.Format(CultureInfo.CurrentCulture, text, args);
    }

    public string Get(Msg? msg)
    {
        if (msg is null) return "";
        if (msg.Key == "raw") return msg.Args.Length > 0 ? msg.Args[0] : "";
        return Format(msg.Key, msg.Args.Cast<object>().ToArray());
    }
}

/// <summary>{loc:Tr key} binds text to the active language.</summary>
[MarkupExtensionReturnType(typeof(object))]
public sealed class TrExtension(string key) : MarkupExtension
{
    public string Key { get; set; } = key;

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Binding($"[{Key}]") { Source = Loc.Instance, Mode = BindingMode.OneWay }.ProvideValue(serviceProvider);
}

/// <summary>Resolves a key held in data, such as a navigation group title.</summary>
public sealed class LocConverter : IValueConverter
{
    public static LocConverter Instance { get; } = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => Loc.Instance[value as string ?? ""];

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
