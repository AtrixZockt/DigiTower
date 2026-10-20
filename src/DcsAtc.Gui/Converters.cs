using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace DcsAtc.Gui;

/// <summary>Visible when true (or, with the parameter "not", when false).</summary>
public sealed class VisibleWhen : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool on = value switch { bool b => b, string s => s.Length > 0, int n => n > 0, null => false, _ => true };
        if (parameter as string == "not") on = !on;
        return on ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>The navigation's radio buttons: checked when the value is the parameter; checking one sets it.</summary>
public sealed class Is : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value?.ToString() == parameter as string;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Enum.Parse(targetType, (string)parameter!) : Binding.DoNothing;
}

/// <summary>A rule over the content when there is something above it (a count above zero).</summary>
public sealed class TopRuleWhen : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => new Thickness(0, value is int n && n > 0 ? 1 : 0, 0, 0);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>True when false (an "off" check box bound to an "on" setting).</summary>
public sealed class Not : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
}

public static class CollectionSync
{
    /// <summary>
    /// Makes <paramref name="target"/> show <paramref name="source"/> in its order, updating the items it already has
    /// (so the window keeps its scroll position and nothing flickers).
    /// </summary>
    public static void Sync<TVm, TSource>(this ObservableCollection<TVm> target, IReadOnlyList<TSource> source, Func<TVm, string> keyOfVm,
        Func<TSource, string> keyOfSource, Func<TSource, TVm> create, Action<TVm, TSource> update)
    {
        var wanted = source.Select(keyOfSource).ToHashSet();
        for (int i = target.Count - 1; i >= 0; i--)
            if (!wanted.Contains(keyOfVm(target[i]))) target.RemoveAt(i);
        for (int i = 0; i < source.Count; i++)
        {
            string key = keyOfSource(source[i]);
            int at = -1;
            for (int j = i; j < target.Count; j++)
                if (keyOfVm(target[j]) == key) { at = j; break; }
            if (at < 0) target.Insert(i, create(source[i]));
            else
            {
                if (at != i) target.Move(at, i);
                update(target[i], source[i]);
            }
        }
    }
}
