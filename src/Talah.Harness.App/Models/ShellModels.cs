using System.ComponentModel;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.UI;
using Microsoft.UI.Xaml.Media;
using Talah.Harness.Application;
using Talah.Harness.App.Services;
using Talah.Harness.Contracts;

namespace Talah.Harness.App.Models;

public abstract class ObservableState : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}

public sealed class KernelDisplayState : ObservableState
{
    private string _status = "STARTING";
    private string _detail = "Starting profile";
    private Brush _indicatorBrush = ShellBrushes.Checking;

    public KernelDisplayState(string adapterId, string name)
    {
        AdapterId = adapterId;
        Name = name;
    }

    public string AdapterId { get; }
    public string Name { get; }
    public string Status { get => _status; set => SetField(ref _status, value); }
    public string Detail { get => _detail; set => SetField(ref _detail, value); }
    public Brush IndicatorBrush { get => _indicatorBrush; set => SetField(ref _indicatorBrush, value); }
    public ProfileRuntimeState? Runtime { get; private set; }

    public void Apply(ProfileRuntimeState state)
    {
        Runtime = state;
        KernelAvailability availability = state.Snapshot?.Health.Availability ?? KernelAvailability.Failed;
        Status = availability.ToString().ToUpperInvariant();
        Detail = state.StartupFailure ?? state.Snapshot?.Health.Summary ?? "Profile did not start.";
        IndicatorBrush = availability switch
        {
            KernelAvailability.Ready => ShellBrushes.Ready,
            KernelAvailability.Starting or KernelAvailability.Unknown => ShellBrushes.Checking,
            KernelAvailability.Degraded => ShellBrushes.Warning,
            _ => ShellBrushes.Missing
        };
    }
}

public sealed class SessionDisplayState
{
    public SessionDisplayState(KernelSessionSummary summary)
    {
        Summary = summary;
        AccentBrush = ShellBrushes.ForAdapter(summary.Session.AdapterId);
    }

    public KernelSessionSummary Summary { get; }
    public string SessionId => Summary.Session.NativeSessionId;
    public string KernelName => Summary.Session.AdapterId.ToUpperInvariant();
    public string Title => Summary.Title;
    public string Preview => string.IsNullOrWhiteSpace(Summary.Preview) ? Summary.Status.ToString() : Summary.Preview;
    public string UpdatedLabel => Summary.UpdatedAt.ToLocalTime().ToString("MMM d · HH:mm", System.Globalization.CultureInfo.CurrentCulture);
    public string StatusLabel => Summary.Status.ToString().ToUpperInvariant();
    public Brush AccentBrush { get; }
}

public sealed class TraceDisplayState : ObservableState
{
    private string _body;
    private string _title;
    private bool _isRunning;

    public TraceDisplayState(
        string itemId, string kindLabel, string title, string body, DateTimeOffset timestamp,
        string glyph, Brush accentBrush, bool isRunning)
    {
        ItemId = itemId;
        KindLabel = kindLabel;
        _title = title;
        _body = body;
        Timestamp = timestamp.ToLocalTime().ToString("HH:mm:ss", System.Globalization.CultureInfo.CurrentCulture);
        Glyph = glyph;
        AccentBrush = accentBrush;
        _isRunning = isRunning;
    }

    public string ItemId { get; }
    public string KindLabel { get; }
    public string Title { get => _title; set => SetField(ref _title, value); }
    public string Body { get => _body; set => SetField(ref _body, value); }
    public string Timestamp { get; }
    public string Glyph { get; }
    public Brush AccentBrush { get; }
    public bool IsRunning { get => _isRunning; set => SetField(ref _isRunning, value); }

    public void Append(string delta) => Body += delta;
}

public sealed class AttachmentDisplayState
{
    public AttachmentDisplayState() { }
    public AttachmentDisplayState(string fullPath) => FullPath = fullPath;
    public string FullPath { get; set; } = string.Empty;
    public string Name => Path.GetFileName(FullPath);
}

public static class TraceProjection
{
    private static readonly Regex SensitiveText = new(
        "(?i)\\b(api[_ -]?key|access[_ -]?token|refresh[_ -]?token|secret|password|credential)\\b(\\s*[:=]\\s*)([^\\s,;]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    public static TraceDisplayState FromItem(KernelItem item, string adapterId)
    {
        string body = string.Join(Environment.NewLine, item.Content.Select(RenderContent).Where(value => !string.IsNullOrWhiteSpace(value)));
        return new TraceDisplayState(item.NativeItemId, Label(item.Kind), item.Title ?? DefaultTitle(item.Kind), body,
            item.CreatedAt, Glyph(item.Kind), ShellBrushes.ForAdapter(adapterId),
            item.Status is KernelItemStatus.Pending or KernelItemStatus.Running);
    }

    public static string RenderContent(ContentBlock block) => block switch
    {
        TextContentBlock text => RedactText(text.Text),
        ReasoningContentBlock reasoning => reasoning.IsOpaque ? "Reasoning is opaque for this kernel." : RedactText(reasoning.Text),
        ToolCallContentBlock call => $"{call.ToolName}{Environment.NewLine}{RenderJson(call.Arguments)}",
        ToolResultContentBlock result => RedactText(result.Output) + (result.IsTruncated ? Environment.NewLine + "Output truncated by kernel." : string.Empty),
        FileChangeContentBlock file => $"{file.ChangeKind}: {file.Path}{(string.IsNullOrWhiteSpace(file.Summary) ? string.Empty : Environment.NewLine + file.Summary)}",
        DiffContentBlock diff => RedactText(diff.UnifiedDiff) + (diff.IsTruncated ? Environment.NewLine + "Diff truncated by kernel." : string.Empty),
        ImageContentBlock image => image.AltText ?? image.Uri,
        NoticeContentBlock notice => $"{notice.Title}: {RedactText(notice.Message)}",
        _ => string.Empty
    };

    public static string RedactText(string value) => SensitiveText.Replace(value, "$1$2[REDACTED]");

    public static string RenderJson(JsonElement value)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
            WriteSanitized(writer, value);
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void WriteSanitized(Utf8JsonWriter writer, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (JsonProperty property in value.EnumerateObject())
            {
                writer.WritePropertyName(property.Name);
                if (IsSensitiveName(property.Name)) writer.WriteStringValue("[REDACTED]");
                else WriteSanitized(writer, property.Value);
            }
            writer.WriteEndObject();
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            foreach (JsonElement item in value.EnumerateArray()) WriteSanitized(writer, item);
            writer.WriteEndArray();
        }
        else if (value.ValueKind == JsonValueKind.String)
        {
            writer.WriteStringValue(RedactText(value.GetString() ?? string.Empty));
        }
        else value.WriteTo(writer);
    }

    private static bool IsSensitiveName(string name) =>
        name.Contains("key", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("token", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("password", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("credential", StringComparison.OrdinalIgnoreCase);

    public static TraceDisplayState FromEvent(DurableKernelEvent durable) => durable.Event.Data switch
    {
        ItemEventData item => FromItem(item.Item, durable.Event.AdapterId),
        PermissionEventData permission => new TraceDisplayState(
            durable.Event.NativeItemId ?? permission.Request.PermissionId, "DECISION", permission.Request.Title,
            permission.Request.Reason ?? "Kernel permission is required.", durable.Event.Timestamp, "\uE7BA", ShellBrushes.Decision, true),
        ElicitationEventData elicitation => new TraceDisplayState(
            durable.Event.NativeItemId ?? elicitation.Request.RequestId, "QUESTION", elicitation.Request.Title,
            elicitation.Request.Prompt, durable.Event.Timestamp, "\uE897", ShellBrushes.Decision, true),
        UsageEventData usage => new TraceDisplayState(
            $"usage-{durable.HostSequence}", "USAGE", "Token and cost update", FormatUsage(usage),
            durable.Event.Timestamp, "\uE9D9", ShellBrushes.ForAdapter(durable.Event.AdapterId), false),
        DiagnosticEventData diagnostic => new TraceDisplayState(
            $"diagnostic-{durable.HostSequence}", "DIAGNOSTIC", diagnostic.Diagnostic.Code,
            diagnostic.Diagnostic.Message, durable.Event.Timestamp, "\uE7BA", ShellBrushes.Warning, false),
        TurnEventData turn => new TraceDisplayState(
            $"turn-{durable.Event.NativeTurnId}-{durable.HostSequence}", "TURN", turn.Status.ToString(),
            turn.Summary ?? "Kernel turn state changed.", durable.Event.Timestamp, "\uE768", ShellBrushes.ForAdapter(durable.Event.AdapterId), turn.Status == TurnStatus.Running),
        StatusEventData status => new TraceDisplayState(
            $"status-{durable.HostSequence}", "KERNEL", status.Availability.ToString(), status.Summary,
            durable.Event.Timestamp, "\uE946", ShellBrushes.ForAdapter(durable.Event.AdapterId), false),
        SessionEventData session => new TraceDisplayState(
            $"session-{durable.HostSequence}", "SESSION", session.Session.Title, session.Session.Status.ToString(),
            durable.Event.Timestamp, "\uE8A5", ShellBrushes.ForAdapter(durable.Event.AdapterId), false),
        ContentDeltaEventData delta => new TraceDisplayState(
            durable.Event.NativeItemId ?? $"delta-{durable.Event.NativeTurnId}-{delta.Channel}", delta.Channel.ToUpperInvariant(),
            delta.Channel, delta.Delta, durable.Event.Timestamp, "\uE8A5", ShellBrushes.ForAdapter(durable.Event.AdapterId), true),
        _ => new TraceDisplayState($"event-{durable.HostSequence}", "EVENT", durable.Event.Kind.ToString(),
            "Kernel state changed.", durable.Event.Timestamp, "\uE946", ShellBrushes.ForAdapter(durable.Event.AdapterId), false)
    };

    public static string FormatUsage(UsageEventData usage)
    {
        var parts = new List<string>();
        if (usage.InputTokens is not null) parts.Add($"input {usage.InputTokens:N0}");
        if (usage.OutputTokens is not null) parts.Add($"output {usage.OutputTokens:N0}");
        if (usage.CachedInputTokens is not null) parts.Add($"cached {usage.CachedInputTokens:N0}");
        if (usage.EstimatedCost is not null) parts.Add($"estimated {usage.EstimatedCost:N4} {usage.Currency ?? string.Empty}".Trim());
        return parts.Count == 0 ? "Kernel did not provide usage totals." : string.Join(" · ", parts);
    }

    private static string Label(KernelItemKind kind) => kind switch
    {
        KernelItemKind.UserMessage => "PROMPT",
        KernelItemKind.AssistantMessage => "RESPONSE",
        KernelItemKind.ToolCall => "TOOL",
        KernelItemKind.ToolResult => "RESULT",
        KernelItemKind.FileChange => "FILE",
        KernelItemKind.CommandExecution => "COMMAND",
        KernelItemKind.Approval => "DECISION",
        _ => kind.ToString().ToUpperInvariant()
    };

    private static string DefaultTitle(KernelItemKind kind) => kind switch
    {
        KernelItemKind.UserMessage => "User instruction",
        KernelItemKind.AssistantMessage => "Kernel response",
        KernelItemKind.Reasoning => "Reasoning",
        KernelItemKind.Plan => "Plan",
        KernelItemKind.ToolCall => "Tool call",
        KernelItemKind.ToolResult => "Tool result",
        KernelItemKind.CommandExecution => "Command execution",
        KernelItemKind.FileChange => "File change",
        KernelItemKind.Diff => "Unified diff",
        KernelItemKind.Approval => "Permission decision",
        KernelItemKind.Question => "Kernel question",
        KernelItemKind.Subagent => "Subagent",
        KernelItemKind.Error => "Error",
        _ => "Notice"
    };

    private static string Glyph(KernelItemKind kind) => kind switch
    {
        KernelItemKind.UserMessage => "\uE8BD",
        KernelItemKind.ToolCall or KernelItemKind.CommandExecution => "\uE756",
        KernelItemKind.ToolResult => "\uE73E",
        KernelItemKind.FileChange or KernelItemKind.Diff => "\uE8A5",
        KernelItemKind.Approval or KernelItemKind.Question => "\uE7BA",
        KernelItemKind.Error => "\uEA39",
        _ => "\uE8A5"
    };
}

public static class ShellBrushes
{
    public static Brush Checking { get; } = Brush(216, 185, 86);
    public static Brush Ready { get; } = Brush(63, 167, 124);
    public static Brush Missing { get; } = Brush(217, 81, 78);
    public static Brush Warning { get; } = Brush(217, 154, 69);
    public static Brush Decision { get; } = Brush(216, 185, 86);
    public static Brush Codex { get; } = Brush(98, 199, 229);
    public static Brush OpenCode { get; } = Brush(216, 185, 86);
    public static Brush Tlah { get; } = Brush(63, 167, 124);

    public static Brush ForAdapter(string adapterId) => adapterId.ToLowerInvariant() switch
    {
        "codex" => Codex,
        "opencode" => OpenCode,
        "tlah" => Tlah,
        _ => Checking
    };

    private static Brush Brush(byte red, byte green, byte blue) =>
        new SolidColorBrush(ColorHelper.FromArgb(255, red, green, blue));
}
