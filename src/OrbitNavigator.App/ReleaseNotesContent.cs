using System.IO;

namespace OrbitNavigator.App;

/// <summary>One bundled, offline source of release notes; no profile or network access.</summary>
internal static class ReleaseNotesContent
{
    internal const string ResourceName = "OrbitNavigator.App.CHANGELOG.md";
    internal const int MaximumCharacters = 131072;

    public static string Read()
    {
        using var stream = typeof(ReleaseNotesContent).Assembly.GetManifestResourceStream(ResourceName);
        if (stream is null) return "The changelog is unavailable in this build.";
        using var reader = new StreamReader(stream);
        var buffer = new char[MaximumCharacters + 1];
        var length = reader.ReadBlock(buffer, 0, buffer.Length);
        return length > MaximumCharacters
            ? "The changelog could not be displayed in this build."
            : new string(buffer, 0, length);
    }

    // This deliberately supports only the headings, paragraphs and bullets in
    // our bundled document. It never interprets HTML, links or executable markup.
    internal static IReadOnlyList<ReleaseNoteBlock> GetDisplayBlocks()
    {
        var blocks = new List<ReleaseNoteBlock>();
        string? paragraph = null;
        var isBullet = false;
        void Flush()
        {
            if (paragraph is not null) blocks.Add(new(paragraph, false, isBullet));
            paragraph = null;
        }
        foreach (var rawLine in Read().Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Equals("## Maintaining this file", StringComparison.Ordinal)) break;
            if (line.StartsWith("# ", StringComparison.Ordinal)) continue;
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                Flush();
                blocks.Add(new(line[3..], true, false));
            }
            else if (line.Length == 0) Flush();
            else if (line.StartsWith("- ", StringComparison.Ordinal))
            {
                Flush();
                isBullet = true;
                paragraph = line[2..];
            }
            else if (paragraph is null)
            {
                isBullet = false;
                paragraph = line;
            }
            else paragraph += " " + line;
        }
        Flush();
        return blocks;
    }
}

internal sealed record ReleaseNoteBlock(string Text, bool IsHeading, bool IsBullet);
