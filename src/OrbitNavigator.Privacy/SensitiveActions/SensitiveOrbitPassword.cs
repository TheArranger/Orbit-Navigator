using System.Security.Cryptography;
using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Infrastructure;
using OrbitNavigator.Contracts.Privacy;

namespace OrbitNavigator.Privacy.SensitiveActions;

/// <summary>A one-owner plaintext password buffer that clears its backing memory.</summary>
public sealed class SensitiveOrbitPassword : IDisposable
{
    private char[]? _characters;

    internal SensitiveOrbitPassword(char[] characters)
    {
        _characters = characters;
    }

    public ReadOnlySpan<char> Characters =>
        _characters is { } characters
            ? characters
            : throw new ObjectDisposedException(nameof(SensitiveOrbitPassword));

    public bool IsDisposed => _characters is null;

    public void Dispose()
    {
        var characters = Interlocked.Exchange(ref _characters, null);
        if (characters is not null)
        {
            CryptographicOperations.ZeroMemory(
                System.Runtime.InteropServices.MemoryMarshal.AsBytes(characters.AsSpan()));
        }
    }
}

public interface IOrbitPasswordSecretLeaseConsumer
{
    ControllerResult<SensitiveOrbitPassword> Consume(OrbitPasswordSecretLeaseId leaseId);
}

public sealed record OrbitPasswordSecretLeaseReceipt(OrbitPasswordSecretLeaseId LeaseId);

/// <summary>
/// Process-local, one-shot password lease store. UI code passes only lease identifiers
/// into shared controller contracts.
/// </summary>
public sealed class OrbitPasswordSecretLeaseStore : IOrbitPasswordSecretLeaseConsumer, IDisposable
{
    public const int MinimumPasswordCharacters = 8;
    public const int MaximumPasswordCharacters = 256;

    private readonly IClock _clock;
    private readonly TimeSpan _leaseLifetime;
    private readonly object _gate = new();
    private readonly Dictionary<OrbitPasswordSecretLeaseId, Lease> _leases = [];
    private bool _disposed;

    public OrbitPasswordSecretLeaseStore(IClock clock, TimeSpan? leaseLifetime = null)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _leaseLifetime = leaseLifetime ?? TimeSpan.FromMinutes(2);
        if (_leaseLifetime <= TimeSpan.Zero || _leaseLifetime > TimeSpan.FromMinutes(5))
        {
            throw new ArgumentOutOfRangeException(nameof(leaseLifetime));
        }
    }

    public ControllerResult<OrbitPasswordSecretLeaseReceipt> Issue(
        ProfileId profileId,
        ReadOnlySpan<char> password)
    {
        if (profileId.IsEmpty ||
            password.Length is < MinimumPasswordCharacters or > MaximumPasswordCharacters)
        {
            return ControllerResult<OrbitPasswordSecretLeaseReceipt>.Failure(ControllerError.Create(
                ControllerErrorCode.InvalidRequest,
                "error.orbit-password.secret-invalid"));
        }

        lock (_gate)
        {
            ThrowIfDisposed();
            RemoveExpired();
            var id = new OrbitPasswordSecretLeaseId(profileId, Guid.NewGuid());
            _leases.Add(id, new Lease(password.ToArray(), _clock.UtcNow.Add(_leaseLifetime)));
            return ControllerResult<OrbitPasswordSecretLeaseReceipt>.Success(
                new OrbitPasswordSecretLeaseReceipt(id));
        }
    }

    public ControllerResult<SensitiveOrbitPassword> Consume(OrbitPasswordSecretLeaseId leaseId)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            RemoveExpired();
            if (leaseId.IsEmpty || !_leases.Remove(leaseId, out var lease))
            {
                return ControllerResult<SensitiveOrbitPassword>.Failure(ControllerError.Create(
                    ControllerErrorCode.Expired,
                    "error.orbit-password.secret-expired"));
            }

            return ControllerResult<SensitiveOrbitPassword>.Success(lease.Transfer());
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            foreach (var lease in _leases.Values)
            {
                lease.Dispose();
            }

            _leases.Clear();
            _disposed = true;
        }
    }

    private void RemoveExpired()
    {
        var expired = _leases
            .Where(pair => pair.Value.ExpiresAtUtc <= _clock.UtcNow)
            .Select(pair => pair.Key)
            .ToArray();
        foreach (var id in expired)
        {
            var lease = _leases[id];
            _leases.Remove(id);
            lease.Dispose();
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private sealed class Lease : IDisposable
    {
        private char[]? _characters;

        public Lease(char[] characters, DateTimeOffset expiresAtUtc)
        {
            _characters = characters;
            ExpiresAtUtc = expiresAtUtc;
        }

        public DateTimeOffset ExpiresAtUtc { get; }

        public SensitiveOrbitPassword Transfer()
        {
            var characters = Interlocked.Exchange(ref _characters, null) ??
                throw new ObjectDisposedException(nameof(Lease));
            return new SensitiveOrbitPassword(characters);
        }

        public void Dispose()
        {
            var characters = Interlocked.Exchange(ref _characters, null);
            if (characters is not null)
            {
                CryptographicOperations.ZeroMemory(
                    System.Runtime.InteropServices.MemoryMarshal.AsBytes(characters.AsSpan()));
            }
        }
    }
}
