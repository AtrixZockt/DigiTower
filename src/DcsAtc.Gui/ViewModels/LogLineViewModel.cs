using System.Windows.Media;
using DcsAtc.Host;

namespace DcsAtc.Gui.ViewModels;

/// <summary>One line of the log as shown: time, category in its colour, text in the level's colour.</summary>
public sealed class LogLineViewModel(LogEntry entry)
{
    public LogEntry Entry { get; } = entry;
    public string Time => Entry.Time.ToString("HH:mm:ss");
    public string Category => AtcLog.Label(Entry.Category);
    public Brush CategoryBrush => Palette.Category(Entry.Category);
    public string Text => Entry.Text;

    public Brush TextBrush => Entry.Level switch
    {
        LogLevel.Error => Palette.ErrorText,
        LogLevel.Warning => Palette.WarnText,
        LogLevel.Debug => Palette.TextDim,
        _ => Palette.Text,
    };

    public Brush RowBrush => Entry.Level switch
    {
        LogLevel.Error => Palette.ErrorRow,
        LogLevel.Warning => Palette.WarnRow,
        _ => Palette.Transparent,
    };

    /// <summary>As copied: "17:42:08  GROUND     text".</summary>
    public override string ToString() => $"{Time}  {Category,-9}  {(Entry.Level is LogLevel.Warning or LogLevel.Error ? Entry.Level.ToString().ToUpperInvariant() + "  " : "")}{Text}";
}
