using System.Collections;
using System.Globalization;
using System.Reflection;
using LineSimulator.App.Mvvm;
using LineSimulator.Models;

namespace LineSimulator.App.ViewModels;

public enum EditorKind
{
    Text,
    Bool,
    Choice
}

/// <summary>
/// Voce del pannello proprietà generico: legge i metadati <see cref="UiPropertyAttribute"/> per riflessione,
/// converte da/verso testo e notifica le modifiche (che fanno ricalcolare scena 3D, validazione e simulazione).
/// </summary>
public sealed class PropertyItemViewModel : ObservableObject
{
    private readonly object _target;
    private readonly PropertyInfo _prop;
    private readonly Action<PropertyItemViewModel> _changed;
    private string? _error;
    private string _text = string.Empty;

    public PropertyItemViewModel(object target, PropertyInfo prop, UiPropertyAttribute meta,
        Action<PropertyItemViewModel> changed, IReadOnlyList<string>? idOptions)
    {
        _target = target;
        _prop = prop;
        Meta = meta;
        _changed = changed;
        var t = prop.PropertyType;
        if (t == typeof(bool)) Kind = EditorKind.Bool;
        else if (t.IsEnum)
        {
            Kind = EditorKind.Choice;
            Options = Enum.GetNames(t).ToList();
        }
        else if (idOptions != null && t == typeof(string))
        {
            Kind = EditorKind.Choice;
            Options = new[] { "" }.Concat(idOptions).ToList();
        }
        else Kind = EditorKind.Text;
        _text = Format(prop.GetValue(target));
    }

    public UiPropertyAttribute Meta { get; }
    public string Name => _prop.Name;
    public string DisplayName => string.IsNullOrEmpty(Meta.Unit) ? Meta.DisplayName : $"{Meta.DisplayName} [{Meta.Unit}]";
    public string Category => Meta.Category;
    public bool IsReadOnly => Meta.ReadOnly || !_prop.CanWrite;
    public bool IsEditable => !IsReadOnly;
    public EditorKind Kind { get; }
    public IReadOnlyList<string>? Options { get; }

    public string? Error
    {
        get => _error;
        private set => Set(ref _error, value);
    }

    public string TextValue
    {
        get => _text;
        set
        {
            if (IsReadOnly) return;
            try
            {
                var parsed = ParseValue(value, _prop.PropertyType);
                _prop.SetValue(_target, parsed);
                Error = null;
                _text = Format(_prop.GetValue(_target));
                Raise();
                _changed(this);
            }
            catch (Exception ex)
            {
                var inner = ex is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex;
                Error = inner.Message;
                _text = value;
                Raise();
            }
        }
    }

    public bool BoolValue
    {
        get => _prop.GetValue(_target) is true;
        set
        {
            if (IsReadOnly) return;
            _prop.SetValue(_target, value);
            Raise();
            _changed(this);
        }
    }

    public string? ChoiceValue
    {
        get => _prop.GetValue(_target)?.ToString();
        set
        {
            if (IsReadOnly || value == null) return;
            var t = _prop.PropertyType;
            _prop.SetValue(_target, t.IsEnum ? Enum.Parse(t, value) : value);
            Raise();
            _changed(this);
        }
    }

    /// <summary>Riallinea il valore mostrato (es. proprietà calcolate dopo una modifica).</summary>
    public void Refresh()
    {
        _text = Format(_prop.GetValue(_target));
        Raise(nameof(TextValue));
        Raise(nameof(BoolValue));
        Raise(nameof(ChoiceValue));
    }

    private static string Format(object? v) => v switch
    {
        null => string.Empty,
        double d when double.IsPositiveInfinity(d) => "∞",
        double d => d.ToString("0.######", CultureInfo.CurrentCulture),
        IList<double> list => string.Join("; ", list.Select(x => x.ToString("0.###", CultureInfo.CurrentCulture))),
        IList<string> ls => string.Join("; ", ls),
        IFormattable f => f.ToString(null, CultureInfo.CurrentCulture),
        _ => v.ToString() ?? string.Empty
    };

    private static double ParseDouble(string s)
    {
        s = s.Trim();
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.CurrentCulture, out var v)) return v;
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) return v;
        throw new FormatException($"'{s}' non è un numero valido.");
    }

    private static object? ParseValue(string text, Type t)
    {
        if (t == typeof(string)) return text;
        if (t == typeof(double)) return ParseDouble(text);
        if (t == typeof(int))
            return int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out var i) ? i
                : throw new FormatException($"'{text}' non è un intero valido.");
        if (t == typeof(List<double>))
            return text.Split(new[] { ';', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries).Select(ParseDouble).ToList();
        if (t == typeof(List<string>))
            return text.Split(new[] { ';', ',', ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).ToList();
        if (t.IsEnum) return Enum.Parse(t, text, true);
        throw new NotSupportedException($"Tipo non modificabile: {t.Name}");
    }

    /// <summary>Crea le voci per tutte le proprietà con <see cref="UiPropertyAttribute"/> dell'oggetto.</summary>
    public static List<PropertyItemViewModel> For(object target, Action<PropertyItemViewModel> changed, Func<string, IReadOnlyList<string>?> idOptions)
    {
        return target.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => (p, a: p.GetCustomAttribute<UiPropertyAttribute>()))
            .Where(x => x.a != null)
            .OrderBy(x => x.a!.Order)
            .Select(x => new PropertyItemViewModel(target, x.p, x.a!, changed, idOptions(x.p.Name)))
            .ToList();
    }
}
