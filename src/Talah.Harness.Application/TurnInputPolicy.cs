using System.Text;
using Talah.Harness.Contracts;

namespace Talah.Harness.Application;

internal static class TurnInputPolicy
{
    private const int MaximumBlocks = 256;
    private const int MaximumReferencedPaths = 256;
    private const int MaximumInputBytes = 8 * 1024 * 1024;
    private const int MaximumPathCharacters = 32_767;

    public static void Validate(TurnInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(input.Content);
        if (input.Content.Count > MaximumBlocks)
            throw new ArgumentException($"A turn cannot contain more than {MaximumBlocks} content blocks.", nameof(input));
        if (input.ReferencedPaths?.Count > MaximumReferencedPaths)
            throw new ArgumentException($"A turn cannot reference more than {MaximumReferencedPaths} paths.", nameof(input));

        long bytes = 0;
        foreach (ContentBlock block in input.Content)
        {
            bytes = checked(bytes + (block switch
            {
                TextContentBlock text => Encoding.UTF8.GetByteCount(text.Text),
                ImageContentBlock image => Encoding.UTF8.GetByteCount(image.Uri),
                _ => throw new ArgumentException($"Content block '{block.GetType().Name}' is not valid user turn input.", nameof(input))
            }));
            if (bytes > MaximumInputBytes)
                throw new ArgumentException($"Turn input exceeds the {MaximumInputBytes} byte limit.", nameof(input));
        }

        if (input.ReferencedPaths is not null)
        {
            foreach (string path in input.ReferencedPaths)
            {
                if (path.Length > MaximumPathCharacters)
                    throw new ArgumentException("A referenced path exceeds the Windows path framing limit.", nameof(input));
                bytes = checked(bytes + Encoding.UTF8.GetByteCount(path));
                if (bytes > MaximumInputBytes)
                    throw new ArgumentException($"Turn input exceeds the {MaximumInputBytes} byte limit.", nameof(input));
            }
        }

        if (input.Content.Count == 0 && (input.ReferencedPaths is null || input.ReferencedPaths.Count == 0))
            throw new ArgumentException("A turn requires text, an image, or a referenced workspace path.", nameof(input));
    }
}
