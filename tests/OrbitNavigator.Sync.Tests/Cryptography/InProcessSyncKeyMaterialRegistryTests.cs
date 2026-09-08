using System.Security.Cryptography;
using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Sync;
using OrbitNavigator.Sync.Cryptography;
using Xunit;

namespace OrbitNavigator.Sync.Tests.Cryptography;

public sealed class InProcessSyncKeyMaterialRegistryTests
{
    [Fact]
    public void RegisterRequiresExactly256Bits()
    {
        using var registry = new InProcessSyncKeyMaterialRegistry();

        Assert.Throws<ArgumentException>(() => registry.Register(Profile, new byte[31]));
        Assert.Throws<ArgumentException>(() => registry.Register(Profile, new byte[33]));
        Assert.Throws<ArgumentException>(() => registry.Register(default, new byte[32]));
        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public void HandlesAreOpaqueUniqueAndIndividuallyRemovable()
    {
        using var registry = new InProcessSyncKeyMaterialRegistry();
        var first = registry.Register(Profile, RandomNumberGenerator.GetBytes(32));
        var second = registry.Register(Profile, RandomNumberGenerator.GetBytes(32));

        Assert.NotEqual(first, second);
        Assert.NotEqual(Guid.Empty, first.Value);
        Assert.Equal(2, registry.Count);
        Assert.True(registry.Remove(first));
        Assert.False(registry.Remove(first));
        Assert.Equal(1, registry.Count);
    }

    [Fact]
    public void DisposeInvalidatesEveryHandleAndRejectsNewKeyMaterial()
    {
        var registry = new InProcessSyncKeyMaterialRegistry();
        registry.Register(Profile, RandomNumberGenerator.GetBytes(32));
        registry.Register(Profile, RandomNumberGenerator.GetBytes(32));

        registry.Dispose();

        Assert.Equal(0, registry.Count);
        Assert.Throws<ObjectDisposedException>(() =>
            registry.Register(Profile, RandomNumberGenerator.GetBytes(32)));
    }

    [Fact]
    public void RegistryOwnsItsKeyCopy()
    {
        using var registry = new InProcessSyncKeyMaterialRegistry();
        var callerKey = RandomNumberGenerator.GetBytes(32);
        var handle = registry.Register(Profile, callerKey);

        CryptographicOperations.ZeroMemory(callerKey);

        Assert.NotEqual(Guid.Empty, handle.Value);
        Assert.Equal(1, registry.Count);
    }

    private static readonly ProfileId Profile = new(Guid.NewGuid());
}
