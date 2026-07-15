using System.ComponentModel;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace AgentLiveWidget;

/// <summary>A single row in the recent-activity log, with a live relative timestamp.</summary>
public sealed class EventItem : INotifyPropertyChanged
{
    public string Icon { get; }
    public string Label { get; }
    public long Timestamp { get; }
    public Brush IconBrush { get; }

    private string _timeAgo = "";
    public string TimeAgo
    {
        get => _timeAgo;
        private set
        {
            if (_timeAgo == value) return;
            _timeAgo = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TimeAgo)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public EventItem(string type, string icon, string label, long timestamp)
    {
        Icon = icon;
        Label = label;
        Timestamp = timestamp;
        IconBrush = ColorFor(type, icon);
        UpdateTimeAgo();
    }

    public void UpdateTimeAgo()
    {
        var seconds = (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - Timestamp) / 1000;
        TimeAgo = seconds switch
        {
            < 3 => "now",
            < 60 => $"{seconds}s",
            < 3600 => $"{seconds / 60}m",
            < 86400 => $"{seconds / 3600}h",
            _ => $"{seconds / 86400}d",
        };
    }

    // Mirrors the status palette used by the energy core / status dot, so the
    // log reads as an extension of the same color language rather than a
    // flat, undifferentiated list.
    private static readonly Brush GreenBrush = Freeze(0x34, 0xD3, 0x99);   // idle / success
    private static readonly Brush VioletBrush = Freeze(0xA7, 0x8B, 0xFA); // thinking
    private static readonly Brush IndigoBrush = Freeze(0x81, 0x8C, 0xF8); // memory save
    private static readonly Brush CyanBrush = Freeze(0x22, 0xD3, 0xEE);   // running tool
    private static readonly Brush AmberBrush = Freeze(0xFB, 0xBF, 0x24); // waiting approval
    private static readonly Brush OrangeBrush = Freeze(0xF5, 0x9E, 0x0B); // context warning
    private static readonly Brush RedBrush = Freeze(0xF8, 0x71, 0x71);   // offline
    private static readonly Brush GrayBrush = Freeze(0x7E, 0x89, 0xA3);  // neutral / unknown

    private static Brush Freeze(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private static Brush ColorFor(string type, string icon) => icon switch
    {
        "❯" => VioletBrush,
        "▶" => CyanBrush,
        "✓" => GreenBrush,
        "✦" => IndigoBrush,
        "◆" => AmberBrush,
        "⚠" => OrangeBrush,
        "✗" => RedBrush,
        "●" => type == "SessionEnd" ? RedBrush : GreenBrush,
        _ => GrayBrush,
    };
}
