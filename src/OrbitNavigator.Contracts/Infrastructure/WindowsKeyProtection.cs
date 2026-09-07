using OrbitNavigator.Contracts.Common;

namespace OrbitNavigator.Contracts.Infrastructure;

public enum WindowsKeyProtectionPurpose
{
    ProfileStorageKey = 0,
    SyncKeysetWrappingKey = 1,
    LocalPasswordVaultKey = 2,
    UserPresenceLedgerKey = 3,
    MyOrbitConnectionCredential = 4,
}

public sealed class ProtectedKeyBlob
{
    private readonly byte[] _bytes;

    private ProtectedKeyBlob(string format, byte[] bytes)
    {
        Format = format;
        _bytes = bytes;
    }

    public string Format { get; }

    public ReadOnlyMemory<byte> Bytes => _bytes;

    public static ControllerResult<ProtectedKeyBlob> Create(
        string format,
        ReadOnlySpan<byte> bytes)
    {
        if (string.IsNullOrWhiteSpace(format) || format.Length > 64 ||
            format.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '-' or '_')) ||
            bytes.IsEmpty)
        {
            return ControllerResult<ProtectedKeyBlob>.Failure(ControllerError.Create(
                ControllerErrorCode.InvalidRequest,
                "error.key_protection.blob_invalid"));
        }

        return ControllerResult<ProtectedKeyBlob>.Success(new ProtectedKeyBlob(
            format,
            bytes.ToArray()));
    }
}

public interface IUnprotectedKeyMaterial : IDisposable
{
    ReadOnlyMemory<byte> Bytes { get; }
}

public sealed record ProtectKeyRequest(
    PrivacyContext Context,
    WindowsKeyProtectionPurpose Purpose,
    ReadOnlyMemory<byte> Plaintext)
{
    public ProfileId ProfileId => Context.ProfileId;

    public bool IsStructurallyValid =>
        Context is { IsStructurallyValid: true } &&
        Enum.IsDefined(Purpose) &&
        !Plaintext.IsEmpty;
}

public sealed record UnprotectKeyRequest(
    PrivacyContext Context,
    WindowsKeyProtectionPurpose Purpose,
    ProtectedKeyBlob ProtectedBlob)
{
    public ProfileId ProfileId => Context.ProfileId;

    public bool IsStructurallyValid =>
        Context is { IsStructurallyValid: true } &&
        Enum.IsDefined(Purpose) &&
        ProtectedBlob is not null;
}

public interface IWindowsKeyProtection
{
    ValueTask<ControllerResult<ProtectedKeyBlob>> ProtectAsync(
        ProtectKeyRequest request,
        CancellationToken cancellationToken = default);

    ValueTask<ControllerResult<IUnprotectedKeyMaterial>> UnprotectAsync(
        UnprotectKeyRequest request,
        CancellationToken cancellationToken = default);
}
