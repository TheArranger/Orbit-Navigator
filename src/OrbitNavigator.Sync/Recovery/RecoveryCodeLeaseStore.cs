using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Sync;

namespace OrbitNavigator.Sync.Recovery;

/// <summary>
/// A profile-bound, process-local recovery-code lease. The printable code may be
/// copied for its one-time presentation before the lease is claimed. Disposal
/// overwrites the complete backing character buffer.
/// </summary>
public sealed class RecoveryCodeLeaseBuffer : SensitiveRecoveryCodeBuffer
{
    private readonly object _gate = new();
    private readonly char[] _code;
    private Action<RecoveryCodeLeaseId>? _removeFromStore;
    private bool _claimed;
    private bool _disposed;

    internal RecoveryCodeLeaseBuffer(
        RecoveryCodeLeaseId leaseId,
        ProfileId profileId,
        char[] code,
        Action<RecoveryCodeLeaseId> removeFromStore)
        : base(leaseId)
    {
        ProfileId = profileId;
        _code = code;
        _removeFromStore = removeFromStore;
    }

    public ProfileId ProfileId { get; }

    public int CodeLength => RecoveryCodeLeaseStore.FormattedCodeLength;

    public override bool IsDisposed
    {
        get
        {
            lock (_gate)
                return _disposed;
        }
    }

    /// <summary>Copies the code for its initial user-visible backup presentation.</summary>
    public void CopyCodeTo(Span<char> destination)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_claimed)
                throw new InvalidOperationException("A claimed recovery-code lease cannot be copied.");
            if (destination.Length < _code.Length)
                throw new ArgumentException("The destination is too small.", nameof(destination));

            _code.CopyTo(destination);
        }
    }

    internal bool TryClaim()
    {
        lock (_gate)
        {
            if (_disposed || _claimed)
                return false;

            _claimed = true;
            _removeFromStore = null;
            return true;
        }
    }

    internal T UseClaimedCode<T>(RecoveryCodeReader<T> reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_claimed)
                throw new InvalidOperationException("The recovery-code lease has not been claimed.");

            return reader(_code);
        }
    }

    public override void Dispose()
    {
        Action<RecoveryCodeLeaseId>? remove;
        lock (_gate)
        {
            if (_disposed)
                return;

            CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(_code.AsSpan()));
            _disposed = true;
            remove = _removeFromStore;
            _removeFromStore = null;
        }

        remove?.Invoke(LeaseId);
    }
}

internal delegate T RecoveryCodeReader<T>(ReadOnlySpan<char> code);

/// <summary>
/// Owns one-shot recovery-code leases. A lease can be consumed exactly once and
/// only by the profile that created or imported it.
/// </summary>
public sealed class RecoveryCodeLeaseStore : IDisposable
{
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    private const int SymbolCount = 20;
    private const int GroupSize = 4;
    internal const int FormattedCodeLength = SymbolCount + ((SymbolCount / GroupSize) - 1);

    private readonly ConcurrentDictionary<RecoveryCodeLeaseId, RecoveryCodeLeaseBuffer> _leases = new();
    private int _disposed;

    public ControllerResult<RecoveryCodeLeaseBuffer> Generate(ProfileId profileId)
    {
        if (profileId.IsEmpty || IsDisposed)
            return ControllerResult<RecoveryCodeLeaseBuffer>.Failure(InvalidLeaseRequest());

        var code = new char[FormattedCodeLength];
        var symbolIndex = 0;
        for (var index = 0; index < code.Length; index++)
        {
            if ((index + 1) % (GroupSize + 1) == 0)
            {
                code[index] = '-';
                continue;
            }

            code[index] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
            symbolIndex++;
        }

        if (symbolIndex != SymbolCount)
        {
            CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(code.AsSpan()));
            return ControllerResult<RecoveryCodeLeaseBuffer>.Failure(InternalLeaseError());
        }

        return Add(profileId, code);
    }

    public ControllerResult<RecoveryCodeLeaseBuffer> Import(
        ProfileId profileId,
        ReadOnlySpan<char> userInput)
    {
        if (profileId.IsEmpty || IsDisposed)
            return ControllerResult<RecoveryCodeLeaseBuffer>.Failure(InvalidLeaseRequest());

        var code = new char[FormattedCodeLength];
        var symbols = 0;
        var added = false;
        try
        {
            foreach (var input in userInput)
            {
                if (input == '-' || char.IsWhiteSpace(input))
                    continue;

                var normalized = char.ToUpperInvariant(input);
                if (symbols >= SymbolCount || Alphabet.IndexOf(normalized) < 0)
                    return ControllerResult<RecoveryCodeLeaseBuffer>.Failure(InvalidCodeFormat());

                var destination = symbols + (symbols / GroupSize);
                code[destination] = normalized;
                symbols++;
            }

            if (symbols != SymbolCount)
                return ControllerResult<RecoveryCodeLeaseBuffer>.Failure(InvalidCodeFormat());

            for (var group = 1; group < SymbolCount / GroupSize; group++)
                code[(group * GroupSize) + (group - 1)] = '-';

            var result = Add(profileId, code);
            added = result.IsSuccess;
            return result;
        }
        finally
        {
            if (!added)
                CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(code.AsSpan()));
        }
    }

    internal ControllerResult<RecoveryCodeLeaseBuffer> Consume(
        ProfileId profileId,
        RecoveryCodeLeaseId leaseId)
    {
        if (profileId.IsEmpty || !leaseId.IsDefined || IsDisposed)
            return ControllerResult<RecoveryCodeLeaseBuffer>.Failure(InvalidLeaseRequest());

        if (!_leases.TryGetValue(leaseId, out var existing))
            return ControllerResult<RecoveryCodeLeaseBuffer>.Failure(UnavailableLease());

        if (existing.ProfileId != profileId)
        {
            return ControllerResult<RecoveryCodeLeaseBuffer>.Failure(
                ControllerError.Create(
                    ControllerErrorCode.IntegrityFailure,
                    "sync.recovery.lease-profile-mismatch"));
        }

        if (!_leases.TryRemove(leaseId, out var lease))
            return ControllerResult<RecoveryCodeLeaseBuffer>.Failure(UnavailableLease());

        if (IsDisposed || !lease.TryClaim())
        {
            lease.Dispose();
            return ControllerResult<RecoveryCodeLeaseBuffer>.Failure(UnavailableLease());
        }

        return ControllerResult<RecoveryCodeLeaseBuffer>.Success(lease);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        foreach (var lease in _leases.Values)
            lease.Dispose();
        _leases.Clear();
    }

    private bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    private ControllerResult<RecoveryCodeLeaseBuffer> Add(ProfileId profileId, char[] code)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            if (IsDisposed)
                break;

            var leaseId = new RecoveryCodeLeaseId(Guid.NewGuid());
            var lease = new RecoveryCodeLeaseBuffer(leaseId, profileId, code, Remove);
            if (_leases.TryAdd(leaseId, lease))
            {
                if (!IsDisposed)
                    return ControllerResult<RecoveryCodeLeaseBuffer>.Success(lease);

                lease.Dispose();
                break;
            }
        }

        CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(code.AsSpan()));
        return ControllerResult<RecoveryCodeLeaseBuffer>.Failure(InternalLeaseError());
    }

    private void Remove(RecoveryCodeLeaseId leaseId) => _leases.TryRemove(leaseId, out _);

    private static ControllerError InvalidLeaseRequest() =>
        ControllerError.Create(
            ControllerErrorCode.InvalidRequest,
            "sync.recovery.lease-request-invalid");

    private static ControllerError InvalidCodeFormat() =>
        ControllerError.Create(
            ControllerErrorCode.InvalidRequest,
            "sync.recovery.code-format-invalid");

    private static ControllerError UnavailableLease() =>
        ControllerError.Create(
            ControllerErrorCode.AlreadyHandled,
            "sync.recovery.lease-unavailable");

    private static ControllerError InternalLeaseError() =>
        ControllerError.Create(
            ControllerErrorCode.InternalFailure,
            "sync.recovery.lease-internal-failure");
}
