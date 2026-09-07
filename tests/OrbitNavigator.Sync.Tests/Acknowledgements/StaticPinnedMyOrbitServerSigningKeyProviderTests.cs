using System.Security.Cryptography;
using System.Runtime.InteropServices;
using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Sync;
using OrbitNavigator.Sync.Acknowledgements;
using Xunit;

namespace OrbitNavigator.Sync.Tests.Acknowledgements;

public sealed class StaticPinnedMyOrbitServerSigningKeyProviderTests
{
    [Fact]
    public async Task ReturnsOnlyConfiguredPinWithDefensiveKeyCopy()
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pinned = Key(signer);
        var provider = new StaticPinnedMyOrbitServerSigningKeyProvider([pinned]);

        var first = await provider.GetPinnedKeyAsync(pinned.KeyId, CancellationToken.None);
        Assert.True(MemoryMarshal.TryGetArray(
            first.Value!.SubjectPublicKeyInfo,
            out ArraySegment<byte> exposed));
        exposed.Array![exposed.Offset] ^= 0xff;
        var second = await provider.GetPinnedKeyAsync(pinned.KeyId, CancellationToken.None);

        Assert.True(first.IsSuccess);
        Assert.True(second.IsSuccess);
        Assert.NotEqual(exposed.Array[exposed.Offset], second.Value!.SubjectPublicKeyInfo.Span[0]);
        Assert.Equal(pinned.KeyId, second.Value.KeyId);
    }

    [Fact]
    public async Task UnknownAndCancelledLookupsFailClosed()
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pinned = Key(signer);
        var provider = new StaticPinnedMyOrbitServerSigningKeyProvider([pinned]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var unknown = await provider.GetPinnedKeyAsync(
            new MyOrbitServerSigningKeyId(Guid.NewGuid()),
            CancellationToken.None);
        var cancelled = await provider.GetPinnedKeyAsync(pinned.KeyId, cancellation.Token);

        Assert.False(unknown.IsSuccess);
        Assert.Equal(ControllerErrorCode.NotFound, unknown.Error!.Code);
        Assert.False(cancelled.IsSuccess);
        Assert.Equal(ControllerErrorCode.Cancelled, cancelled.Error!.Code);
    }

    [Fact]
    public void RejectsDuplicateOrNonP256Pins()
    {
        using var p256 = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        var valid = Key(p256);
        var invalidCurve = MyOrbitServerSigningPublicKey.Create(
            new MyOrbitServerSigningKeyId(Guid.NewGuid()),
            MyOrbitServerSigningAlgorithm.EcdsaP256Sha256P1363,
            p384.ExportSubjectPublicKeyInfo(),
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(30),
            MyOrbitServerSigningKeyState.Active).Value!;

        Assert.Throws<ArgumentException>(() =>
            new StaticPinnedMyOrbitServerSigningKeyProvider([valid, valid]));
        Assert.Throws<ArgumentException>(() =>
            new StaticPinnedMyOrbitServerSigningKeyProvider([invalidCurve]));
    }

    private static MyOrbitServerSigningPublicKey Key(ECDsa signer) =>
        MyOrbitServerSigningPublicKey.Create(
            new MyOrbitServerSigningKeyId(Guid.NewGuid()),
            MyOrbitServerSigningAlgorithm.EcdsaP256Sha256P1363,
            signer.ExportSubjectPublicKeyInfo(),
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(30),
            MyOrbitServerSigningKeyState.Active).Value!;
}
