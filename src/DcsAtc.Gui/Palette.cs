using System.Windows.Media;
using DcsAtc.Host;

namespace DcsAtc.Gui;

/// <summary>The colours the view models choose from (the rest are in Theme.xaml).</summary>
public static class Palette
{
    private static SolidColorBrush Brush(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }

    public static readonly Brush Text = Brush("#E0DEDA"), TextDim = Brush("#A7A9A6"), Transparent = Brushes.Transparent;
    public static readonly Brush WarnText = Brush("#FFD27A"), WarnRow = Brush("#2A2110");
    public static readonly Brush ErrorText = Brush("#FF9C8F"), ErrorRow = Brush("#2C1512");
    public static readonly Brush DepTag = Brush("#1E4E7A"), DepTagText = Brush("#E3F1FF"), ArrTag = Brush("#8A5A12"), ArrTagText = Brush("#FFF1D6");
    public static readonly Brush AiTag = Brush("#3F5F33"), AiTagText = Brush("#E8F7E0");

    /// <summary>A category's colour in the log.</summary>
    public static Brush Category(LogCategory c) => Categories[c];

    private static readonly Dictionary<LogCategory, Brush> Categories = new()
    {
        [LogCategory.App] = Brush("#C9C7C3"),
        [LogCategory.Bridge] = Brush("#C9C7C3"),
        [LogCategory.Ground] = Brush("#25DEFF"),
        [LogCategory.Tower] = Brush("#FFC24A"),
        [LogCategory.Approach] = Brush("#B9A6FF"),
        [LogCategory.Atis] = Brush("#8ED8F8"),
        [LogCategory.Ai] = Brush("#9ED38A"),
        [LogCategory.Farp] = Brush("#7FE0C8"),
        [LogCategory.VoiceIn] = Brush("#F2A8C8"),
        [LogCategory.VoiceOut] = Brush("#E7B7D6"),
        [LogCategory.Srs] = Brush("#C9C7C3"),
    };

    /// <summary>A status light: background, edge, label, text.</summary>
    public static (Brush Back, Brush Edge, Brush Label, Brush Text) Light(Light state) => state switch
    {
        Host.Light.On => (Brush("#11291A"), Brush("#2E6B40"), Brush("#7CF29A"), Brush("#CFEFD8")),
        Host.Light.Warning => (Brush("#33260A"), Brush("#7A5A14"), Brush("#FFC24A"), Brush("#F2DFB5")),
        Host.Light.Error => (Brush("#2C1512"), Brush("#A8433A"), Brush("#FF9C8F"), Brush("#FFD9D4")),
        _ => (Brush("#262625"), Brush("#45474A"), Brush("#A7A9A6"), Brush("#A7A9A6")),
    };
}
