using System.Text;
using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Infrastructure;
using OrbitNavigator.Contracts.Privacy;
using OrbitNavigator.Privacy.SensitiveActions;
using Xunit;

namespace OrbitNavigator.Privacy.Tests.SensitiveActions;

public sealed class SensitiveActionSecurityTests
{
    [Fact]
    public void PasswordLeasesAreProfileBoundOneShotAndExpiring()
    {
        var clock = new MutableClock();
        using var leases = new OrbitPasswordSecretLeaseStore(clock, TimeSpan.FromMinutes(1));
        var profile = new ProfileId(Guid.NewGuid());
        var issued = leases.Issue(profile, "correct horse battery staple".AsSpan());

        var first = leases.Consume(issued.Value!.LeaseId);
        var replay = leases.Consume(issued.Value.LeaseId);
        using var password = first.Value!;
        var expiring = leases.Issue(profile, "another long password".AsSpan());
        clock.Advance(TimeSpan.FromMinutes(2));
        var expired = leases.Consume(expiring.Value!.LeaseId);

        Assert.True(first.IsSuccess);
        Assert.Equal("correct horse battery staple", new string(password.Characters));
        Assert.Equal(ControllerErrorCode.Expired, replay.Error?.Code);
        Assert.Equal(ControllerErrorCode.Expired, expired.Error?.Code);
    }

    [Fact]
    public async Task CredentialVerifierIsSaltedAndWindowsProtectedBeforeProfileStorage()
    {
        var clock = new MutableClock();
        var storage = new MemoryProfileStorage();
        var protection = new TestKeyProtection();
        var credentials = new ProtectedOrbitPasswordCredentialStore(
            storage,
            protection,
            clock,
            new OrbitPasswordKdfOptions(OrbitPasswordKdfOptions.MinimumIterations));
        using var leases = new OrbitPasswordSecretLeaseStore(clock);
        var context = Browsing().Privacy;

        using (var password = Consume(leases, context.ProfileId, "local orbit password"))
        {
            var configured = await credentials.SetAsync(
                context,
                password,
                OrbitPasswordCredentialWriteMode.RequireAbsent,
                default);
            Assert.True(configured.IsSuccess);
            Assert.Equal(OrbitPasswordVaultStatus.Configured, configured.Value!.Status);
        }

        var rawText = Encoding.UTF8.GetString(storage.RawPayload!);
        Assert.DoesNotContain("local orbit password", rawText, StringComparison.Ordinal);
        Assert.Equal(WindowsKeyProtectionPurpose.LocalPasswordVaultKey, protection.LastProtectPurpose);

        using var correct = Consume(leases, context.ProfileId, "local orbit password");
        using var wrong = Consume(leases, context.ProfileId, "different orbit password");
        var accepted = await credentials.VerifyAsync(context, correct, default);
        var rejected = await credentials.VerifyAsync(context, wrong, default);

        Assert.True(accepted.Value!.IsConfigured);
        Assert.True(accepted.Value.Matches);
        Assert.True(rejected.Value!.IsConfigured);
        Assert.False(rejected.Value.Matches);
        Assert.Equal(WindowsKeyProtectionPurpose.LocalPasswordVaultKey, protection.LastUnprotectPurpose);
    }

    [Fact]
    public async Task PrivateContextCannotMutatePersistentOrbitPasswordVault()
    {
        var clock = new MutableClock();
        var storage = new MemoryProfileStorage();
        var credentials = new ProtectedOrbitPasswordCredentialStore(
            storage,
            new TestKeyProtection(),
            clock,
            new OrbitPasswordKdfOptions(OrbitPasswordKdfOptions.MinimumIterations));
        using var leases = new OrbitPasswordSecretLeaseStore(clock);
        var context = Browsing().Privacy with { Mode = BrowserProfileMode.Private };
        using var password = Consume(leases, context.ProfileId, "private cannot persist");

        var configured = await credentials.SetAsync(
            context,
            password,
            OrbitPasswordCredentialWriteMode.RequireAbsent,
            default);
        var deleted = await credentials.DeleteAsync(context, default);

        Assert.Equal(ControllerErrorCode.PolicyDenied, configured.Error?.Code);
        Assert.Equal(ControllerErrorCode.PolicyDenied, deleted.Error?.Code);
        Assert.Equal(0, storage.ReadCalls);
        Assert.Equal(0, storage.WriteCalls);
        Assert.Equal(0, storage.DeleteCalls);
    }

    [Fact]
    public async Task AuthorizationIsPurposeSessionAndProfileBoundAndOneShot()
    {
        var fixture = await AuthorizerFixture.CreateAsync();
        using (fixture)
        {
            var token = await fixture.AuthorizeAsync(SensitiveActionKind.ViewSavedPasswords);
            var wrongContext = fixture.Context with
            {
                Privacy = fixture.Context.Privacy with { SessionId = new BrowserSessionId(Guid.NewGuid()) },
            };
            var wrong = await fixture.Authorizer.ValidateAndConsumeAsync(
                new ValidateSensitiveActionAuthorizationRequest(
                    wrongContext,
                    token,
                    SensitiveActionKind.ViewSavedPasswords));
            var accepted = await fixture.Authorizer.ValidateAndConsumeAsync(
                new ValidateSensitiveActionAuthorizationRequest(
                    fixture.Context,
                    token,
                    SensitiveActionKind.ViewSavedPasswords));
            var replay = await fixture.Authorizer.ValidateAndConsumeAsync(
                new ValidateSensitiveActionAuthorizationRequest(
                    fixture.Context,
                    token,
                    SensitiveActionKind.ViewSavedPasswords));

            Assert.Equal(ControllerErrorCode.InvalidRequest, wrong.Error?.Code);
            Assert.True(accepted.IsSuccess);
            Assert.Equal(ControllerErrorCode.AlreadyHandled, replay.Error?.Code);
        }
    }

    [Fact]
    public async Task ExpiredAuthorizationCannotBeConsumed()
    {
        var fixture = await AuthorizerFixture.CreateAsync();
        using (fixture)
        {
            var token = await fixture.AuthorizeAsync(
                SensitiveActionKind.AutofillSavedPassword,
                TimeSpan.FromSeconds(30));
            fixture.Clock.Advance(TimeSpan.FromMinutes(1));

            var result = await fixture.Authorizer.ValidateAndConsumeAsync(
                new ValidateSensitiveActionAuthorizationRequest(
                    fixture.Context,
                    token,
                    SensitiveActionKind.AutofillSavedPassword));

            Assert.Equal(ControllerErrorCode.Expired, result.Error?.Code);
        }
    }

    [Fact]
    public async Task ForgottenResetConsumesWindowsProofDeletesOnlyLocalVaultAndRevokesTokens()
    {
        var clock = new MutableClock();
        var credentials = new ResetCredentialStore();
        var authorizer = new ResetAuthorizer(clock);
        var presence = new ResetPresence(clock);
        var eraser = new ResetEraser();
        var vault = new OrbitPasswordVault(
            credentials,
            new NeverConsumeLease(),
            authorizer,
            presence,
            eraser,
            clock);
        var context = Browsing();
        var proof = WindowsUserPresenceProof.Create(
            new WindowsUserPresenceProofId(Guid.NewGuid()),
            WindowsUserPresencePurpose.ForgottenOrbitPasswordVaultReset,
            context.Privacy.ProfileId,
            context.Privacy.SessionId,
            clock.UtcNow,
            clock.UtcNow.AddMinutes(1)).Value!;
        var request = ForgottenOrbitPasswordVaultResetRequest.Create(context, proof, true).Value!;

        var reset = await vault.ResetForgottenPasswordAsync(request);

        Assert.True(reset.IsSuccess);
        Assert.True(reset.Value!.LocalSavedPasswordsDeleted);
        Assert.False(reset.Value.SyncedDataAffected);
        Assert.False(reset.Value.DownloadedFilesDeleted);
        Assert.True(presence.Consumed);
        Assert.True(eraser.Called);
        Assert.True(credentials.DeleteCalled);
        Assert.Equal(SensitiveAuthorizationRevocationReason.VaultReset, authorizer.LastReason);
    }

    private static SensitiveOrbitPassword Consume(
        OrbitPasswordSecretLeaseStore leases,
        ProfileId profileId,
        string password) =>
        leases.Consume(leases.Issue(profileId, password.AsSpan()).Value!.LeaseId).Value!;

    private static BrowsingContext Browsing() =>
        new(
            new PrivacyContext(
                new ProfileId(Guid.NewGuid()),
                new BrowserSessionId(Guid.NewGuid()),
                BrowserProfileMode.Normal),
            new BrowserWindowId(Guid.NewGuid()),
            new BrowserTabId(Guid.NewGuid()),
            null);

    private sealed class MutableClock : IClock
    {
        public DateTimeOffset UtcNow { get; private set; } =
            DateTimeOffset.Parse("2026-08-08T12:00:00Z");

        public void Advance(TimeSpan duration) => UtcNow = UtcNow.Add(duration);
    }

    private sealed class AuthorizerFixture : IDisposable
    {
        private AuthorizerFixture(
            MutableClock clock,
            MemoryProfileStorage storage,
            OrbitPasswordSecretLeaseStore leases,
            ProtectedOrbitPasswordCredentialStore credentials,
            SensitiveActionAuthorizer authorizer,
            BrowsingContext context)
        {
            Clock = clock;
            Storage = storage;
            Leases = leases;
            Credentials = credentials;
            Authorizer = authorizer;
            Context = context;
        }

        public MutableClock Clock { get; }
        public MemoryProfileStorage Storage { get; }
        public OrbitPasswordSecretLeaseStore Leases { get; }
        public ProtectedOrbitPasswordCredentialStore Credentials { get; }
        public SensitiveActionAuthorizer Authorizer { get; }
        public BrowsingContext Context { get; }

        public static async Task<AuthorizerFixture> CreateAsync()
        {
            var clock = new MutableClock();
            var storage = new MemoryProfileStorage();
            var leases = new OrbitPasswordSecretLeaseStore(clock);
            var credentials = new ProtectedOrbitPasswordCredentialStore(
                storage,
                new TestKeyProtection(),
                clock,
                new OrbitPasswordKdfOptions(OrbitPasswordKdfOptions.MinimumIterations));
            var authorizer = new SensitiveActionAuthorizer(leases, credentials, clock);
            var context = Browsing();
            using var password = Consume(leases, context.Privacy.ProfileId, "configured orbit password");
            var configured = await credentials.SetAsync(
                context.Privacy,
                password,
                OrbitPasswordCredentialWriteMode.RequireAbsent,
                default);
            Assert.True(configured.IsSuccess);
            return new(clock, storage, leases, credentials, authorizer, context);
        }

        public async Task<SensitiveActionAuthorizationToken> AuthorizeAsync(
            SensitiveActionKind purpose,
            TimeSpan? lifetime = null)
        {
            var lease = Leases.Issue(Context.Privacy.ProfileId, "configured orbit password".AsSpan()).Value!;
            var result = await Authorizer.AuthorizeAsync(new SensitiveActionAuthorizationRequest(
                Context,
                purpose,
                lease.LeaseId,
                lifetime ?? TimeSpan.FromMinutes(1)));
            Assert.True(result.IsSuccess);
            return result.Value!;
        }

        public void Dispose() => Leases.Dispose();
    }

    private sealed class MemoryProfileStorage : IProfileStorage
    {
        private ProfileStorageEntry? _entry;
        public byte[]? RawPayload { get; private set; }
        public int ReadCalls { get; private set; }
        public int WriteCalls { get; private set; }
        public int DeleteCalls { get; private set; }

        public ValueTask<ControllerResult<ProfileStorageEntry>> ReadAsync(
            ProfileStorageAddress address,
            CancellationToken cancellationToken = default)
        {
            ReadCalls++;
            return ValueTask.FromResult(_entry is null
                ? ControllerResult<ProfileStorageEntry>.Failure(ControllerError.Create(
                    ControllerErrorCode.NotFound,
                    "test.not-found"))
                : ControllerResult<ProfileStorageEntry>.Success(_entry));
        }

        public ValueTask<ControllerResult<ProfileStorageWriteReceipt>> WriteAsync(
            ProfileStorageWriteRequest request,
            CancellationToken cancellationToken = default)
        {
            WriteCalls++;
            if (request.ExpectedRevision is { } expected && _entry?.Revision != expected)
            {
                return ValueTask.FromResult(ControllerResult<ProfileStorageWriteReceipt>.Failure(
                    ControllerError.Create(ControllerErrorCode.Conflict, "test.conflict")));
            }

            var revision = new ProfileStorageRevision(Guid.NewGuid());
            RawPayload = request.Payload.ToArray();
            _entry = ProfileStorageEntry.Create(revision, RawPayload).Value!;
            return ValueTask.FromResult(ControllerResult<ProfileStorageWriteReceipt>.Success(new(revision)));
        }

        public ValueTask<ControllerResult> DeleteAsync(
            ProfileStorageAddress address,
            ProfileStorageRevision? expectedRevision = null,
            CancellationToken cancellationToken = default)
        {
            DeleteCalls++;
            _entry = null;
            RawPayload = null;
            return ValueTask.FromResult(ControllerResult.Success());
        }
    }

    private sealed class TestKeyProtection : IWindowsKeyProtection
    {
        public WindowsKeyProtectionPurpose? LastProtectPurpose { get; private set; }
        public WindowsKeyProtectionPurpose? LastUnprotectPurpose { get; private set; }

        public ValueTask<ControllerResult<ProtectedKeyBlob>> ProtectAsync(
            ProtectKeyRequest request,
            CancellationToken cancellationToken = default)
        {
            LastProtectPurpose = request.Purpose;
            var bytes = request.Plaintext.ToArray();
            for (var index = 0; index < bytes.Length; index++) bytes[index] ^= 0xA5;
            return ValueTask.FromResult(ProtectedKeyBlob.Create("test-protected-v1", bytes));
        }

        public ValueTask<ControllerResult<IUnprotectedKeyMaterial>> UnprotectAsync(
            UnprotectKeyRequest request,
            CancellationToken cancellationToken = default)
        {
            LastUnprotectPurpose = request.Purpose;
            var bytes = request.ProtectedBlob.Bytes.ToArray();
            for (var index = 0; index < bytes.Length; index++) bytes[index] ^= 0xA5;
            return ValueTask.FromResult(ControllerResult<IUnprotectedKeyMaterial>.Success(
                new TestKeyMaterial(bytes)));
        }
    }

    private sealed class TestKeyMaterial(byte[] bytes) : IUnprotectedKeyMaterial
    {
        private byte[]? _bytes = bytes;
        public ReadOnlyMemory<byte> Bytes => _bytes ?? throw new ObjectDisposedException(nameof(TestKeyMaterial));
        public void Dispose()
        {
            if (_bytes is { } value) Array.Clear(value);
            _bytes = null;
        }
    }

    private sealed class ResetCredentialStore : IOrbitPasswordCredentialStore
    {
        public bool DeleteCalled { get; private set; }

        public ValueTask<ControllerResult<OrbitPasswordVaultState>> DeleteAsync(
            PrivacyContext context,
            CancellationToken cancellationToken)
        {
            DeleteCalled = true;
            return ValueTask.FromResult(State(context.ProfileId, OrbitPasswordVaultStatus.NotConfigured));
        }

        public ValueTask<ControllerResult<OrbitPasswordVaultState>> GetStateAsync(
            PrivacyContext context,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(State(context.ProfileId, OrbitPasswordVaultStatus.Configured));

        public ValueTask<ControllerResult<OrbitPasswordVaultState>> SetAsync(
            PrivacyContext context,
            SensitiveOrbitPassword password,
            OrbitPasswordCredentialWriteMode mode,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(State(context.ProfileId, OrbitPasswordVaultStatus.Configured));

        public ValueTask<ControllerResult<OrbitPasswordVerification>> VerifyAsync(
            PrivacyContext context,
            SensitiveOrbitPassword password,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(ControllerResult<OrbitPasswordVerification>.Success(new(true, true)));

        private static ControllerResult<OrbitPasswordVaultState> State(
            ProfileId profileId,
            OrbitPasswordVaultStatus status) =>
            ControllerResult<OrbitPasswordVaultState>.Success(new(
                profileId,
                status,
                OrbitPasswordVaultDataDisposition.LocalOnlyNeverSync,
                null));
    }

    private sealed class ResetAuthorizer(IClock clock) : ISensitiveActionAuthorizer
    {
        public SensitiveAuthorizationRevocationReason? LastReason { get; private set; }
        public ValueTask<ControllerResult<SensitiveActionAuthorizationToken>> AuthorizeAsync(
            SensitiveActionAuthorizationRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<ControllerResult<SensitiveActionAuthorizationReceipt>> ValidateAndConsumeAsync(
            ValidateSensitiveActionAuthorizationRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<ControllerResult<SensitiveAuthorizationRevocationReceipt>> RevokeSessionAsync(
            PrivacyContext context,
            SensitiveAuthorizationRevocationReason reason,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<ControllerResult<SensitiveAuthorizationRevocationReceipt>> RevokeProfileAsync(
            ProfileId profileId,
            SensitiveAuthorizationRevocationReason reason,
            CancellationToken cancellationToken = default)
        {
            LastReason = reason;
            return ValueTask.FromResult(ControllerResult<SensitiveAuthorizationRevocationReceipt>.Success(new(
                profileId,
                null,
                reason,
                0,
                clock.UtcNow)));
        }
    }

    private sealed class ResetPresence(IClock clock) : IWindowsUserPresenceProofAdapter
    {
        public bool Consumed { get; private set; }
        public ValueTask<ControllerResult<WindowsUserPresenceProof>> IssueAsync(
            WindowsUserPresenceIssueRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<ControllerResult<WindowsUserPresenceValidationReceipt>> ValidateAndConsumeAsync(
            WindowsUserPresenceValidationRequest request,
            CancellationToken cancellationToken = default)
        {
            Consumed = true;
            return ValueTask.FromResult(ControllerResult<WindowsUserPresenceValidationReceipt>.Success(new(
                request.Proof.ProofId,
                clock.UtcNow)));
        }
    }

    private sealed class ResetEraser : ILocalSavedPasswordEraser
    {
        public bool Called { get; private set; }
        public ValueTask<ControllerResult> DeleteAllAsync(
            PrivacyContext context,
            CancellationToken cancellationToken)
        {
            Called = true;
            return ValueTask.FromResult(ControllerResult.Success());
        }
    }

    private sealed class NeverConsumeLease : IOrbitPasswordSecretLeaseConsumer
    {
        public ControllerResult<SensitiveOrbitPassword> Consume(OrbitPasswordSecretLeaseId leaseId) =>
            throw new NotSupportedException();
    }
}
