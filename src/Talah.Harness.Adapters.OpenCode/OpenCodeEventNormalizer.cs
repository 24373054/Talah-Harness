using System.Text.Json;
using Talah.Harness.Contracts;

namespace Talah.Harness.Adapters.OpenCode;

public sealed class OpenCodeEventNormalizer(string profileId)
{
    private readonly string _profileId = profileId;
    private long _sequence;

    public IReadOnlyList<KernelEvent> Normalize(OpenCodeSseEvent source)
    {
        JsonElement root = source.VendorJson;
        string type = String(root, "type") ?? source.Event ?? "unknown";
        JsonElement properties = Element(root, "properties") ?? root;
        string? sessionId = String(properties, "sessionID") ?? String(properties, "sessionId");
        string? messageId = String(properties, "messageID") ?? String(properties, "messageId");
        JsonElement? part = Element(properties, "part");
        sessionId ??= part is JsonElement p ? String(p, "sessionID") : null;
        messageId ??= part is JsonElement p2 ? String(p2, "messageID") : null;
        string? itemId = part is JsonElement p3 ? String(p3, "id") : null;
        var events = new List<KernelEvent>();

        switch (type)
        {
            case "server.connected":
                Add(KernelEventKind.AdapterStatusChanged, new StatusEventData(KernelAvailability.Ready, "OpenCode event stream connected."));
                break;
            case "session.created":
                Add(KernelEventKind.SessionCreated, new SessionEventData(ToSession(Element(properties, "info") ?? properties)));
                break;
            case "session.updated":
                Add(KernelEventKind.SessionMetadataChanged, new SessionEventData(ToSession(Element(properties, "info") ?? properties)));
                break;
            case "session.deleted":
                Add(KernelEventKind.SessionClosed, new TurnEventData(TurnStatus.Completed, "Session deleted."));
                break;
            case "session.idle":
                Add(KernelEventKind.TurnCompleted, new TurnEventData(TurnStatus.Completed));
                break;
            case "session.error":
                Add(KernelEventKind.TurnFailed, new TurnEventData(TurnStatus.Failed, Json(properties)));
                break;
            case "session.status":
                JsonElement? status = Element(properties, "status");
                string? statusType = status is JsonElement st ? String(st, "type") : null;
                Add(statusType == "idle" ? KernelEventKind.TurnCompleted : KernelEventKind.TurnStarted,
                    new TurnEventData(statusType == "idle" ? TurnStatus.Completed : TurnStatus.Running, statusType));
                break;
            case "message.part.delta":
                string? field = String(properties, "field");
                Add(KernelEventKind.ContentDelta,
                    new ContentDeltaEventData(field?.Contains("reason", StringComparison.OrdinalIgnoreCase) == true ? "reasoning" : "assistant",
                        String(properties, "delta") ?? string.Empty));
                break;
            case "message.part.updated":
                if (part is JsonElement updated) AddPart(updated);
                break;
            case "message.updated":
                JsonElement info = Element(properties, "info") ?? properties;
                JsonElement? error = Element(info, "error");
                if (error is not null) Add(KernelEventKind.TurnFailed, new TurnEventData(TurnStatus.Failed, Json(error.Value)));
                AddUsage(info);
                break;
            case "session.diff":
                JsonElement? diff = Element(properties, "diff") ?? Element(properties, "patch");
                Add(KernelEventKind.ItemCompleted, new ItemEventData(new KernelItem(
                    itemId ?? "diff_" + Guid.NewGuid().ToString("N"), KernelItemKind.Diff, KernelItemStatus.Completed,
                    "Workspace diff", [new DiffContentBlock(diff is null ? Json(properties) : Json(diff.Value), [])],
                    DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, VendorData: root.Clone())));
                break;
            case "permission.asked":
            case "permission.v2.asked":
                AddPermission(properties);
                break;
            case "question.asked":
            case "question.v2.asked":
                AddQuestion(properties);
                break;
            case "file.edited":
                string path = String(properties, "file") ?? String(properties, "path") ?? "unknown";
                Add(KernelEventKind.ItemCompleted, new ItemEventData(Item(itemId, KernelItemKind.FileChange,
                    new FileChangeContentBlock(path, "edited"), root)));
                break;
            default:
                Add(KernelEventKind.Diagnostic, new DiagnosticEventData(new KernelDiagnostic(
                    "OpenCode.UnknownEvent", DiagnosticSeverity.Trace, $"Unrecognized OpenCode event '{type}'.", VendorData: root.Clone())));
                break;
        }

        return events;

        void Add(KernelEventKind kind, KernelEventData data)
        {
            string? sourceIdentity = source.Id ?? String(root, "id");
            string? nativeEventId = sourceIdentity is null ? null : $"{sourceIdentity}:{events.Count}:{kind}";
            events.Add(new KernelEvent(
                OpenCodeAdapter.Id, _profileId, sessionId, messageId, itemId,
                Interlocked.Increment(ref _sequence), DateTimeOffset.UtcNow, kind, data, root.Clone(), nativeEventId));
        }

        void AddPart(JsonElement value)
        {
            string partType = String(value, "type") ?? "unknown";
            JsonElement? state = Element(value, "state");
            string? stateType = state is JsonElement stateValue ? String(stateValue, "status") : null;
            KernelItemStatus status = stateType switch { "pending" => KernelItemStatus.Pending, "running" => KernelItemStatus.Running, "error" => KernelItemStatus.Failed, _ => KernelItemStatus.Completed };
            ContentBlock content;
            KernelItemKind kind;
            switch (partType)
            {
                case "text":
                    kind = KernelItemKind.AssistantMessage;
                    content = new TextContentBlock(String(value, "text") ?? string.Empty);
                    break;
                case "reasoning":
                    kind = KernelItemKind.Reasoning;
                    content = new ReasoningContentBlock(String(value, "text") ?? string.Empty);
                    break;
                case "tool":
                    kind = stateType is "completed" or "error" ? KernelItemKind.ToolResult : KernelItemKind.ToolCall;
                    string callId = String(value, "callID") ?? String(value, "id") ?? "unknown";
                    string tool = String(value, "tool") ?? "unknown";
                    JsonElement input = state is JsonElement s ? Element(s, "input") ?? default : default;
                    content = kind == KernelItemKind.ToolCall
                        ? new ToolCallContentBlock(callId, tool, input.ValueKind == JsonValueKind.Undefined ? JsonSerializer.SerializeToElement(new { }) : input.Clone(), String(state ?? value, "title"))
                        : new ToolResultContentBlock(callId, stateType != "error", state is JsonElement sr ? String(sr, "output") ?? Json(sr) : string.Empty);
                    break;
                case "file":
                    kind = KernelItemKind.FileChange;
                    content = new FileChangeContentBlock(String(value, "filename") ?? String(value, "url") ?? "unknown", "referenced");
                    break;
                case "patch":
                    kind = KernelItemKind.Diff;
                    content = new DiffContentBlock(String(value, "hash") ?? Json(value), []);
                    break;
                case "subtask":
                case "agent":
                    kind = KernelItemKind.Subagent;
                    content = new NoticeContentBlock("Subagent", String(value, "description") ?? Json(value), DiagnosticSeverity.Information);
                    break;
                case "step-finish":
                    AddUsage(value);
                    return;
                default:
                    kind = KernelItemKind.Notice;
                    content = new NoticeContentBlock("OpenCode message part", Json(value), DiagnosticSeverity.Trace);
                    break;
            }

            KernelEventKind eventKind = status switch
            {
                KernelItemStatus.Pending or KernelItemStatus.Running => KernelEventKind.ItemStarted,
                KernelItemStatus.Failed => KernelEventKind.ItemCompleted,
                _ => KernelEventKind.ItemCompleted
            };
            Add(eventKind, new ItemEventData(new KernelItem(
                String(value, "id") ?? "part_" + Guid.NewGuid().ToString("N"), kind, status, partType,
                [content], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, VendorData: value.Clone())));
        }

        void AddUsage(JsonElement value)
        {
            JsonElement? tokens = Element(value, "tokens");
            if (tokens is null) return;
            Add(KernelEventKind.UsageUpdated, new UsageEventData(
                Long(tokens.Value, "input"), Long(tokens.Value, "output"), Long(tokens.Value, "cache"),
                Decimal(value, "cost"), "USD"));
        }

        void AddPermission(JsonElement value)
        {
            string id = String(value, "id") ?? String(value, "permissionID") ?? "unknown";
            string sid = String(value, "sessionID") ?? sessionId ?? "unknown";
            string permission = String(value, "permission") ?? String(value, "type") ?? "tool";
            JsonElement? patterns = Element(value, "patterns");
            ResourceImpact[] impacts = patterns is { ValueKind: JsonValueKind.Array }
                ? patterns.Value.EnumerateArray().Select(x => new ResourceImpact(permission, x.ToString(), permission, "unknown")).ToArray()
                : [];
            Add(KernelEventKind.PermissionRequested, new PermissionEventData(new PermissionRequest(
                id, Session(sid), messageId, permission, $"OpenCode requests {permission}", String(value, "reason"), impacts,
                [
                    new PermissionChoice("once", PermissionDecisionKind.AllowOnce, "Allow once", null),
                    new PermissionChoice("always", PermissionDecisionKind.AllowForSession, "Always allow", null),
                    new PermissionChoice("reject", PermissionDecisionKind.Deny, "Reject", null)
                ], value.Clone())));
        }

        void AddQuestion(JsonElement value)
        {
            string id = String(value, "id") ?? "unknown";
            string sid = String(value, "sessionID") ?? sessionId ?? "unknown";
            Add(KernelEventKind.ElicitationRequested, new ElicitationEventData(new ElicitationRequest(
                id, Session(sid), "OpenCode question", String(value, "question") ?? Json(value),
                Element(value, "schema")?.Clone(), value.Clone())));
        }
    }

    public KernelSessionSummary ToSession(JsonElement value)
    {
        string id = String(value, "id") ?? "unknown";
        JsonElement? time = Element(value, "time");
        long? created = time is JsonElement t ? Long(t, "created") : null;
        long? updated = time is JsonElement t2 ? Long(t2, "updated") : null;
        long? archived = time is JsonElement t3 ? Long(t3, "archived") : null;
        return new KernelSessionSummary(Session(id, String(value, "parentID"), String(value, "workspaceID")),
            String(value, "title") ?? id, archived is null ? SessionStatus.Idle : SessionStatus.Archived,
            FromUnix(created), FromUnix(updated), null,
            new Dictionary<string, string> { ["directory"] = String(value, "directory") ?? string.Empty });
    }

    private SessionRef Session(string id, string? parent = null, string? workspace = null)
        => new(OpenCodeAdapter.Id, _profileId, id, parent, workspace);

    private static KernelItem Item(string? id, KernelItemKind kind, ContentBlock content, JsonElement vendor)
        => new(id ?? "item_" + Guid.NewGuid().ToString("N"), kind, KernelItemStatus.Completed, null,
            [content], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, VendorData: vendor.Clone());

    internal static string? String(JsonElement value, string name)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out JsonElement property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;

    internal static JsonElement? Element(JsonElement value, string name)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out JsonElement property) ? property : null;

    private static long? Long(JsonElement value, string name)
        => Element(value, name) is JsonElement item && item.TryGetInt64(out long number) ? number : null;

    private static decimal? Decimal(JsonElement value, string name)
        => Element(value, name) is JsonElement item && item.TryGetDecimal(out decimal number) ? number : null;

    internal static string Json(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.GetRawText();

    private static DateTimeOffset FromUnix(long? milliseconds)
        => milliseconds is > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(milliseconds.Value) : DateTimeOffset.UtcNow;
}
