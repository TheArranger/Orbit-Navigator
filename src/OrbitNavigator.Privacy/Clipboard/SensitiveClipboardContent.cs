using System.Security.Cryptography;
using OrbitNavigator.Contracts.Common;

namespace OrbitNavigator.Privacy.Clipboard;

/// <summary>
/// A short-lived plaintext buffer used only inside the privacy boundary.
/// The backing characters are cleared when the buffer is disposed.
/// </summary>
public sealed class SensitiveClipboardContent : IDisposable
{
    private char[]? _characters;

    private SensitiveClipboardContent(char[] characters)
    {
        _characters = characters;
    }

    public ReadOnlyMemory<char> Characters =>
        _characters is { } characters
            ? characters
            : throw new ObjectDisposedException(nameof(SensitiveClipboardContent));

    public bool IsDisposed => _characters is null;

    public static ControllerResult<SensitiveClipboardContent> Create(ReadOnlySpan<char> characters)
    {
        if (characters.IsEmpty || characters.Length > ClipboardShelfController.MaximumContentCharacters)
        {
            return ControllerResult<SensitiveClipboardContent>.Failure(ControllerError.Create(
                ControllerErrorCode.InvalidRequest,
                "error.clipboard-shelf.content-invalid"));
        }

        return ControllerResult<SensitiveClipboardContent>.Success(
            new SensitiveClipboardContent(characters.ToArray()));
    }

    public void Dispose()
    {
        var characters = Interlocked.Exchange(ref _characters, null);
        if (characters is not null)
        {
            CryptographicOperations.ZeroMemory(System.Runtime.InteropServices.MemoryMarshal.AsBytes(
                characters.AsSpan()));
        }
    }
}

public interface IClipboardCaptureLeaseProvider
{
    ValueTask<ControllerResult<SensitiveClipboardContent>> RedeemAsync(
        BrowsingContext context,
        Contracts.Privacy.ClipboardCaptureLeaseId leaseId,
        CancellationToken cancellationToken);
}

public interface IClipboardShelfUseTarget
{
    ValueTask<ControllerResult> WriteAsync(
        BrowsingContext destination,
        Contracts.Privacy.ClipboardShelfContentKind kind,
        SensitiveClipboardContent content,
        CancellationToken cancellationToken);
}
