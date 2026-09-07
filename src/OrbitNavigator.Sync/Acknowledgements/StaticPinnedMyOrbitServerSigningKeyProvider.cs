using System.Collections.Frozen;
using System.Security.Cryptography;
using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Sync;

namespace OrbitNavigator.Sync.Acknowledgements;

/// <summary>
/// Immutable bootstrap keyring for server-signature verification. The caller
/// supplies only application-bundled pins; no transport response can add a key.
/// Authenticated key rotation can replace this provider when the My Orbit account
/// service supplies its signed rotation-chain adapter.
/// </summary>
public sealed class StaticPinnedMyOrbitServerSigningKeyProvider
    : IMyOrbitServerSigningKeyProvider
{
    private const string P256Oid = "1.2.840.10045.3.1.7";
    private readonly FrozenDictionary<MyOrbitServerSigningKeyId, PinnedKey> _keys;

    public StaticPinnedMyOrbitServerSigningKeyProvider(
        IEnumerable<MyOrbitServerSigningPublicKey> pinnedKeys)
    {
        ArgumentNullException.ThrowIfNull(pinnedKeys);
        var values = pinnedKeys.ToArray();
        if (values.Length == 0 ||
            values.Any(value => value is null || !IsValidP256Key(value)) ||
            values.Select(value => value.KeyId).Distinct().Count() != values.Length)
        {
            throw new ArgumentException(
                "At least one unique valid P-256 My Orbit signing key must be pinned.",
                nameof(pinnedKeys));
        }

        _keys = values.ToFrozenDictionary(
            value => value.KeyId,
            value => new PinnedKey(
                value.KeyId,
                value.Algorithm,
                value.SubjectPublicKeyInfo.ToArray(),
                value.ValidFromUtc,
                value.ValidUntilUtc,
                value.State));
    }

    public ValueTask<ControllerResult<MyOrbitServerSigningPublicKey>> GetPinnedKeyAsync(
        MyOrbitServerSigningKeyId keyId,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromResult(
                ControllerResult<MyOrbitServerSigningPublicKey>.Failure(
                    ControllerError.Create(
                        ControllerErrorCode.Cancelled,
                        "error.sync.server_signing_key.cancelled",
                        isRetryable: true)));
        }

        if (!keyId.IsDefined || !_keys.TryGetValue(keyId, out var pinned))
        {
            return ValueTask.FromResult(
                ControllerResult<MyOrbitServerSigningPublicKey>.Failure(
                    ControllerError.Create(
                        ControllerErrorCode.NotFound,
                        "error.sync.server_signing_key.not_found")));
        }

        var copy = MyOrbitServerSigningPublicKey.Create(
            pinned.KeyId,
            pinned.Algorithm,
            pinned.SubjectPublicKeyInfo,
            pinned.ValidFromUtc,
            pinned.ValidUntilUtc,
            pinned.State);
        return ValueTask.FromResult(copy.IsSuccess
            ? copy
            : ControllerResult<MyOrbitServerSigningPublicKey>.Failure(
                ControllerError.Create(
                    ControllerErrorCode.IntegrityFailure,
                    "error.sync.server_signing_key.invalid")));
    }

    private static bool IsValidP256Key(MyOrbitServerSigningPublicKey key)
    {
        if (!key.KeyId.IsDefined ||
            key.Algorithm != MyOrbitServerSigningAlgorithm.EcdsaP256Sha256P1363 ||
            key.ValidFromUtc >= key.ValidUntilUtc ||
            key.State is not (MyOrbitServerSigningKeyState.Active or
                MyOrbitServerSigningKeyState.Retiring))
        {
            return false;
        }

        try
        {
            using var verifier = ECDsa.Create();
            verifier.ImportSubjectPublicKeyInfo(key.SubjectPublicKeyInfo.Span, out var bytesRead);
            var parameters = verifier.ExportParameters(false);
            return bytesRead == key.SubjectPublicKeyInfo.Length &&
                verifier.KeySize == 256 &&
                parameters.Curve.Oid.Value == P256Oid;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private sealed record PinnedKey(
        MyOrbitServerSigningKeyId KeyId,
        MyOrbitServerSigningAlgorithm Algorithm,
        byte[] SubjectPublicKeyInfo,
        DateTimeOffset ValidFromUtc,
        DateTimeOffset ValidUntilUtc,
        MyOrbitServerSigningKeyState State);
}
