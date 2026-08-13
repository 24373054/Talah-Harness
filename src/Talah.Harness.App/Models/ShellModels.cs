using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI;
using Microsoft.UI.Xaml.Media;

namespace Talah.Harness.App.Models;

public sealed class KernelDisplayState : INotifyPropertyChanged
{
    private string _status;
    private string _detail;
    private Brush _indicatorBrush;

    public KernelDisplayState(string adapterId, string name, string status, string detail, Brush indicatorBrush)
    {
        AdapterId = adapterId;
        Name = name;
        _status = status;
        _detail = detail;
        _indicatorBrush = indicatorBrush;
    }

    public string AdapterId { get; }

    public string Name { get; }

    public string Status
    {
        get => _status;
        set => SetField(ref _status, value);
    }

    public string Detail
    {
        get => _detail;
        set => SetField(ref _detail, value);
    }

    public Brush IndicatorBrush
    {
        get => _indicatorBrush;
        set => SetField(ref _indicatorBrush, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public sealed class SessionDisplayState
{
    public SessionDisplayState(
        string sessionId,
        string kernelName,
        string title,
        string preview,
        string updatedLabel,
        Brush accentBrush)
    {
        SessionId = sessionId;
        KernelName = kernelName;
        Title = title;
        Preview = preview;
        UpdatedLabel = updatedLabel;
        AccentBrush = accentBrush;
    }

    public string SessionId { get; set; }

    public string KernelName { get; set; }

    public string Title { get; set; }

    public string Preview { get; set; }

    public string UpdatedLabel { get; set; }

    public Brush AccentBrush { get; set; }
}

public sealed class TraceDisplayState
{
    public TraceDisplayState(
        string itemId,
        string kindLabel,
        string title,
        string body,
        string timestamp,
        string glyph,
        Brush accentBrush,
        bool isRunning)
    {
        ItemId = itemId;
        KindLabel = kindLabel;
        Title = title;
        Body = body;
        Timestamp = timestamp;
        Glyph = glyph;
        AccentBrush = accentBrush;
        IsRunning = isRunning;
    }

    public string ItemId { get; set; }

    public string KindLabel { get; set; }

    public string Title { get; set; }

    public string Body { get; set; }

    public string Timestamp { get; set; }

    public string Glyph { get; set; }

    public Brush AccentBrush { get; set; }

    public bool IsRunning { get; set; }
}

public static class ShellBrushes
{
    public static Brush Checking { get; } = new SolidColorBrush(ColorHelper.FromArgb(255, 216, 185, 86));

    public static Brush Ready { get; } = new SolidColorBrush(ColorHelper.FromArgb(255, 63, 167, 124));

    public static Brush Missing { get; } = new SolidColorBrush(ColorHelper.FromArgb(255, 217, 81, 78));

    public static Brush Codex { get; } = new SolidColorBrush(ColorHelper.FromArgb(255, 98, 199, 229));

    public static Brush OpenCode { get; } = new SolidColorBrush(ColorHelper.FromArgb(255, 216, 185, 86));

    public static Brush Tlah { get; } = new SolidColorBrush(ColorHelper.FromArgb(255, 244, 247, 250));
}
