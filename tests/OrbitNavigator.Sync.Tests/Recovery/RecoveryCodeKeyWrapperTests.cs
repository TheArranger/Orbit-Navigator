using System.Reflection;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Sync;
using OrbitNavigator.Sync.Recovery;
using Xunit;

namespace OrbitNavigator.Sync.Tests.Recovery;

public sealed class RecoveryCodeKeyWrapperTests
{
    [Fact]
    public void Generated_codes_are_printable_and_outstanding_lease_is_zeroized_on_store_disposal()
    {
        var store = new RecoveryCodeLeaseStore();
        var lease = Required(store.Generate(NewProfile()));
        var backing = PrivateArray<char>(lease, "_code");
        var visible = CopyCode(lease);

        Assert.Matches(
            new Regex("^[ABCDEFGHJKLMNPQRSTUVWXYZ23456789]{4}(?:-[ABCDEFGHJKLMNPQRSTUVWXYZ23456789]{4}){4}$"),
            new string(visible));
        Assert.Contains(backing, value => value != '\0');

        store.Dispose();

        Assert.True(lease.IsDisposed);
        Assert.All(backing, value => Assert.Equal('\0', value));
        CryptographicOperations.ZeroMemory(System.Runtime.InteropServices.MemoryMarshal.AsBytes(visible.AsSpan()));
    }

    [Fact]
    public void Round_trip_consumes_each_lease_once_and_disposes_key_material_by_zeroing()
    {
        using var store = new RecoveryCodeLeaseStore();
        var wrapper = new RecoveryCodeKeyWrapper(store);
        var profile = NewProfile();
        var keyset = new SyncKeysetId(Guid.NewGuid());
        var rootKey = RandomNumberGenerator.GetBytes(UnwrappedSyncRootKey.KeySizeBytes);
        var generated = Required(store.Generate(profile));
        var generatedBacking = PrivateArray<char>(generated, "_code");
        var code = CopyCode(generated);

        var wrapped = wrapper.Wrap(profile, keyset, 7, rootKey, generated.LeaseId);

        Assert.True(wrapped.IsSuccess);
        Assert.True(generated.IsDisposed);
        Assert.All(generatedBacking, value => Assert.Equal('\0', value));
        Assert.Equal(SyncKeyWrapMethod.RecoveryCode, wrapped.Value!.WrapMethod);
        Assert.Equal(SyncKdfAlgorithm.Pbkdf2Sha256, wrapped.Value.Kdf.Algorithm);
        Assert.True(wrapped.Value.Kdf.Iterations >= RecoveryCodeWrappingOptions.MinimumPbkdf2Iterations);
        Assert.Equal(32, wrapped.Value.Kdf.Salt.Length);
        Assert.Equal(12, wrapped.Value.Nonce.Length);
        Assert.Equal(32, wrapped.Value.WrappedKeyCiphertext.Length);
        Assert.Equal(16, wrapped.Value.AuthenticationTag.Length);

        var reused = wrapper.Wrap(profile, keyset, 7, rootKey, generated.LeaseId);
        Assert.False(reused.IsSuccess);
        Assert.Equal(ControllerErrorCode.AlreadyHandled, reused.Error!.Code);

        var imported = Required(store.Import(profile, code));
        var unwrapped = wrapper.Unwrap(profile, wrapped.Value, imported.LeaseId);

        Assert.True(unwrapped.IsSuccess);
        Assert.True(imported.IsDisposed);
        var recovered = new byte[UnwrappedSyncRootKey.KeySizeBytes];
        unwrapped.Value!.CopyTo(recovered);
        Assert.Equal(rootKey, recovered);

        var keyBacking = PrivateArray<byte>(unwrapped.Value, "_key");
        unwrapped.Value.Dispose();
        Assert.True(unwrapped.Value.IsDisposed);
        Assert.All(keyBacking, value => Assert.Equal(0, value));
        Assert.Throws<ObjectDisposedException>(() => unwrapped.Value.CopyTo(new byte[32]));

        CryptographicOperations.ZeroMemory(rootKey);
        CryptographicOperations.ZeroMemory(recovered);
        CryptographicOperations.ZeroMemory(System.Runtime.InteropServices.MemoryMarshal.AsBytes(code.AsSpan()));
    }

    [Fact]
    public void Lease_is_profile_bound_without_being_destroyed_by_wrong_profile()
    {
        using var store = new RecoveryCodeLeaseStore();
        var wrapper = new RecoveryCodeKeyWrapper(store);
        var owner = NewProfile();
        var other = NewProfile();
        var lease = Required(store.Generate(owner));
        var rootKey = RandomNumberGenerator.GetBytes(32);
        var keyset = new SyncKeysetId(Guid.NewGuid());

        var denied = wrapper.Wrap(other, keyset, 0, rootKey, lease.LeaseId);
        var accepted = wrapper.Wrap(owner, keyset, 0, rootKey, lease.LeaseId);

        Assert.False(denied.IsSuccess);
        Assert.Equal(ControllerErrorCode.IntegrityFailure, denied.Error!.Code);
        Assert.True(accepted.IsSuccess);
        Assert.True(lease.IsDisposed);
        CryptographicOperations.ZeroMemory(rootKey);
    }

    [Fact]
    public void Each_wrap_uses_fresh_salt_and_nonce()
    {
        using var store = new RecoveryCodeLeaseStore();
        var wrapper = new RecoveryCodeKeyWrapper(store);
        var profile = NewProfile();
        var keyset = new SyncKeysetId(Guid.NewGuid());
        var rootKey = RandomNumberGenerator.GetBytes(32);
        var firstLease = Required(store.Generate(profile));
        var code = CopyCode(firstLease);

        var first = Required(wrapper.Wrap(profile, keyset, 1, rootKey, firstLease.LeaseId));
        var secondLease = Required(store.Import(profile, code));
        var second = Required(wrapper.Wrap(profile, keyset, 1, rootKey, secondLease.LeaseId));

        Assert.False(first.Kdf.Salt.Span.SequenceEqual(second.Kdf.Salt.Span));
        Assert.False(first.Nonce.Span.SequenceEqual(second.Nonce.Span));
        Assert.False(first.WrappedKeyCiphertext.Span.SequenceEqual(second.WrappedKeyCiphertext.Span));
        CryptographicOperations.ZeroMemory(rootKey);
        CryptographicOperations.ZeroMemory(System.Runtime.InteropServices.MemoryMarshal.AsBytes(code.AsSpan()));
    }

    [Fact]
    public void Wrong_code_tampering_and_profile_mismatch_are_integrity_failures()
    {
        using var store = new RecoveryCodeLeaseStore();
        var wrapper = new RecoveryCodeKeyWrapper(store);
        var profile = NewProfile();
        var keyset = new SyncKeysetId(Guid.NewGuid());
        var rootKey = RandomNumberGenerator.GetBytes(32);
        var sourceLease = Required(store.Generate(profile));
        var correctCode = CopyCode(sourceLease);
        var wrapped = Required(wrapper.Wrap(profile, keyset, 3, rootKey, sourceLease.LeaseId));

        var wrongLease = Required(store.Generate(profile));
        var wrongCode = wrapper.Unwrap(profile, wrapped, wrongLease.LeaseId);
        AssertIntegrityFailure(wrongCode);

        var tamperedCiphertext = wrapped.WrappedKeyCiphertext.ToArray();
        tamperedCiphertext[0] ^= 0x80;
        var tamperLease = Required(store.Import(profile, correctCode));
        var tampered = wrapper.Unwrap(
            profile,
            wrapped with { WrappedKeyCiphertext = tamperedCiphertext },
            tamperLease.LeaseId);
        AssertIntegrityFailure(tampered);

        var metadataLease = Required(store.Import(profile, correctCode));
        var metadataMismatch = wrapper.Unwrap(
            profile,
            wrapped with { Generation = wrapped.Generation + 1 },
            metadataLease.LeaseId);
        AssertIntegrityFailure(metadataMismatch);

        var otherProfile = NewProfile();
        var profileLease = Required(store.Import(otherProfile, correctCode));
        var profileMismatch = wrapper.Unwrap(otherProfile, wrapped, profileLease.LeaseId);
        AssertIntegrityFailure(profileMismatch);

        CryptographicOperations.ZeroMemory(rootKey);
        CryptographicOperations.ZeroMemory(tamperedCiphertext);
        CryptographicOperations.ZeroMemory(System.Runtime.InteropServices.MemoryMarshal.AsBytes(correctCode.AsSpan()));
    }

    [Fact]
    public void Strict_validation_rejects_malformed_wrapped_keysets_before_consuming_lease()
    {
        using var store = new RecoveryCodeLeaseStore();
        var wrapper = new RecoveryCodeKeyWrapper(store);
        var profile = NewProfile();
        var rootKey = RandomNumberGenerator.GetBytes(32);
        var sourceLease = Required(store.Generate(profile));
        var code = CopyCode(sourceLease);
        var wrapped = Required(wrapper.Wrap(
            profile,
            new SyncKeysetId(Guid.NewGuid()),
            2,
            rootKey,
            sourceLease.LeaseId));
        var lease = Required(store.Import(profile, code));

        var invalidValues = new WrappedSyncKeyset[]
        {
            wrapped with { KeysetId = default },
            wrapped with { Generation = -1 },
            wrapped with { WrapMethod = SyncKeyWrapMethod.TrustedDevice },
            wrapped with { Kdf = wrapped.Kdf with { Algorithm = SyncKdfAlgorithm.Argon2id } },
            wrapped with { Kdf = wrapped.Kdf with { Salt = new byte[31] } },
            wrapped with { Kdf = wrapped.Kdf with { Iterations = RecoveryCodeWrappingOptions.MinimumPbkdf2Iterations - 1 } },
            wrapped with { Kdf = wrapped.Kdf with { MemoryKiB = 1 } },
            wrapped with { Kdf = wrapped.Kdf with { Parallelism = 2 } },
            wrapped with { Kdf = wrapped.Kdf with { DerivedKeySizeBytes = 31 } },
            wrapped with { Nonce = new byte[11] },
            wrapped with { WrappedKeyCiphertext = new byte[31] },
            wrapped with { AuthenticationTag = new byte[15] },
        };

        foreach (var invalid in invalidValues)
        {
            var result = wrapper.Unwrap(profile, invalid, lease.LeaseId);
            Assert.False(result.IsSuccess);
            Assert.Equal(ControllerErrorCode.InvalidRequest, result.Error!.Code);
            Assert.False(lease.IsDisposed);
        }

        var valid = wrapper.Unwrap(profile, wrapped, lease.LeaseId);
        Assert.True(valid.IsSuccess);
        valid.Value!.Dispose();
        Assert.True(lease.IsDisposed);
        CryptographicOperations.ZeroMemory(rootKey);
        CryptographicOperations.ZeroMemory(System.Runtime.InteropServices.MemoryMarshal.AsBytes(code.AsSpan()));
    }

    [Fact]
    public async Task Concurrent_use_allows_exactly_one_lease_consumer()
    {
        using var store = new RecoveryCodeLeaseStore();
        var wrapper = new RecoveryCodeKeyWrapper(store);
        var profile = NewProfile();
        var keyset = new SyncKeysetId(Guid.NewGuid());
        var key = RandomNumberGenerator.GetBytes(32);
        var lease = Required(store.Generate(profile));

        var attempts = Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() => wrapper.Wrap(profile, keyset, 1, key, lease.LeaseId)))
            .ToArray();
        var results = await Task.WhenAll(attempts);

        Assert.Single(results, result => result.IsSuccess);
        Assert.Equal(7, results.Count(result =>
            !result.IsSuccess && result.Error!.Code == ControllerErrorCode.AlreadyHandled));
        Assert.True(lease.IsDisposed);
        CryptographicOperations.ZeroMemory(key);
    }

    [Fact]
    public void Invalid_root_key_length_and_weak_options_are_rejected()
    {
        using var store = new RecoveryCodeLeaseStore();
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new RecoveryCodeWrappingOptions(RecoveryCodeWrappingOptions.MinimumPbkdf2Iterations - 1));

        var wrapper = new RecoveryCodeKeyWrapper(store);
        var profile = NewProfile();
        var lease = Required(store.Generate(profile));
        var keyset = new SyncKeysetId(Guid.NewGuid());

        var invalid = wrapper.Wrap(profile, keyset, 1, new byte[31], lease.LeaseId);
        var valid = wrapper.Wrap(profile, keyset, 1, new byte[32], lease.LeaseId);

        Assert.False(invalid.IsSuccess);
        Assert.Equal(ControllerErrorCode.InvalidRequest, invalid.Error!.Code);
        Assert.True(valid.IsSuccess);
    }

    private static ProfileId NewProfile() => new(Guid.NewGuid());

    private static char[] CopyCode(RecoveryCodeLeaseBuffer lease)
    {
        var code = new char[lease.CodeLength];
        lease.CopyCodeTo(code);
        return code;
    }

    private static T Required<T>(ControllerResult<T> result)
        where T : class
    {
        Assert.True(result.IsSuccess);
        return result.Value!;
    }

    private static T[] PrivateArray<T>(object instance, string fieldName) =>
        Assert.IsType<T[]>(instance.GetType()
            .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(instance));

    private static void AssertIntegrityFailure(ControllerResult<UnwrappedSyncRootKey> result)
    {
        Assert.False(result.IsSuccess);
        Assert.Equal(ControllerErrorCode.IntegrityFailure, result.Error!.Code);
    }
}
