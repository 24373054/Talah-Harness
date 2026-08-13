using System.Text.Json;
using System.Text.Json.Serialization;

namespace Talah.Harness.Contracts;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(TextContentBlock), "text")]
[JsonDerivedType(typeof(ReasoningContentBlock), "reasoning")]
[JsonDerivedType(typeof(ToolCallContentBlock), "tool-call")]
[JsonDerivedType(typeof(ToolResultContentBlock), "tool-result")]
[JsonDerivedType(typeof(FileChangeContentBlock), "file-change")]
[JsonDerivedType(typeof(DiffContentBlock), "diff")]
[JsonDerivedType(typeof(ImageContentBlock), "image")]
[JsonDerivedType(typeof(NoticeContentBlock), "notice")]
public abstract record ContentBlock;

public sealed record TextContentBlock(string Text, string Format = "markdown") : ContentBlock;

public sealed record ReasoningContentBlock(string Text, bool IsOpaque = false) : ContentBlock;

public sealed record ToolCallContentBlock(
    string CallId,
    string ToolName,
    JsonElement Arguments,
    string? Summary = null) : ContentBlock;

public sealed record ToolResultContentBlock(
    string CallId,
    bool Succeeded,
    string Output,
    bool IsTruncated = false) : ContentBlock;

public sealed record FileChangeContentBlock(
    string Path,
    string ChangeKind,
    string? Summary = null) : ContentBlock;

public sealed record DiffContentBlock(
    string UnifiedDiff,
    IReadOnlyList<string> Paths,
    bool IsTruncated = false) : ContentBlock;

public sealed record ImageContentBlock(
    string Uri,
    string? MediaType,
    string? AltText) : ContentBlock;

public sealed record NoticeContentBlock(
    string Title,
    string Message,
    DiagnosticSeverity Severity) : ContentBlock;

public enum KernelItemKind
{
    UserMessage,
    AssistantMessage,
    Reasoning,
    Plan,
    ToolCall,
    ToolResult,
    CommandExecution,
    FileChange,
    Diff,
    Approval,
    Question,
    Subagent,
    Error,
    Notice
}

public enum KernelItemStatus
{
    Pending,
    Running,
    Completed,
    Failed,
    Cancelled
}

public sealed record KernelItem(
    string NativeItemId,
    KernelItemKind Kind,
    KernelItemStatus Status,
    string? Title,
    IReadOnlyList<ContentBlock> Content,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? ParentNativeItemId = null,
    JsonElement? VendorData = null);

