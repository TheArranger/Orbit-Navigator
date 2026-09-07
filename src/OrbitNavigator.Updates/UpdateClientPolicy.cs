using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OrbitNavigator.Contracts.Updates;

namespace OrbitNavigator.Updates;

public sealed record UpdateClientState(
    int SchemaVersion,
    string InstallSeedBase64,
    UpdateReleaseChannel Channel,
    bool BetaChannelOptIn,
    string? PrimaryEntityTag,
    string? BetaEntityTag,
    long PrimaryAcceptedReleaseSequence,
    long BetaAcceptedReleaseSequence,
    int ConsecutiveFailures,
    DateTimeOffset? NextCheckNotBeforeUtc)
{
    public const int CurrentSchemaVersion = 1;
    public const int InstallSeedBytes = 32;

    public static UpdateClientState CreateNew()
    {
        Span<byte> seed = stackalloc byte[InstallSeedBytes];
        RandomNumberGenerator.Fill(seed);
        return new UpdateClientState(
            CurrentSchemaVersion,
            Convert.ToBase64String(seed),
            UpdateReleaseChannel.Primary,
            false,
            null,
            null,
            0,
            0,
            0,
            null);
    }

    public UpdateClientState SelectChannel(
        UpdateReleaseChannel channel,
        bool explicitUserOptIn)
    {
        if (!Enum.IsDefined(channel))
        {
            throw new ArgumentOutOfRangeException(nameof(channel));
        }

        if (channel == UpdateReleaseChannel.Beta && !explicitUserOptIn)
        {
            throw new InvalidOperationException(
                "The Beta channel requires an explicit user opt-in.");
        }

        return this with
        {
            Channel = channel,
            BetaChannelOptIn = channel == UpdateReleaseChannel.Beta,
            ConsecutiveFailures = 0,
            NextCheckNotBeforeUtc = null,
        };
    }

    public string? GetEntityTag(UpdateReleaseChannel channel) => channel switch
    {
        UpdateReleaseChannel.Primary => PrimaryEntityTag,
        UpdateReleaseChannel.Beta => BetaEntityTag,
        _ => throw new ArgumentOutOfRangeException(nameof(channel)),
    };

    public UpdateClientState WithEntityTag(UpdateReleaseChannel channel, string? entityTag)
    {
        UpdateConditionalRequest.ValidateEntityTag(entityTag);
        return channel switch
        {
            UpdateReleaseChannel.Primary => this with { PrimaryEntityTag = entityTag },
            UpdateReleaseChannel.Beta => this with { BetaEntityTag = entityTag },
            _ => throw new ArgumentOutOfRangeException(nameof(channel)),
        };
    }

    public long GetAcceptedReleaseSequence(UpdateReleaseChannel channel) => channel switch
    {
        UpdateReleaseChannel.Primary => PrimaryAcceptedReleaseSequence,
        UpdateReleaseChannel.Beta => BetaAcceptedReleaseSequence,
        _ => throw new ArgumentOutOfRangeException(nameof(channel)),
    };

    public UpdateClientState WithAcceptedReleaseSequence(
        UpdateReleaseChannel channel,
        long releaseSequence)
    {
        var current = GetAcceptedReleaseSequence(channel);
        if (releaseSequence < current)
        {
            throw new InvalidOperationException("Update release sequence cannot move backwards.");
        }

        return channel switch
        {
            UpdateReleaseChannel.Primary => this with
            {
                PrimaryAcceptedReleaseSequence = releaseSequence,
            },
            UpdateReleaseChannel.Beta => this with
            {
                BetaAcceptedReleaseSequence = releaseSequence,
            },
            _ => throw new ArgumentOutOfRangeException(nameof(channel)),
        };
    }

    public byte[] GetInstallSeed()
    {
        byte[] seed;
        try
        {
            seed = Convert.FromBase64String(InstallSeedBase64);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException("The local update install seed is invalid.", exception);
        }

        return seed.Length == InstallSeedBytes
            ? seed
            : throw new InvalidDataException("The local update install seed has an invalid length.");
    }

    public void Validate()
    {
        if (SchemaVersion != CurrentSchemaVersion ||
            !Enum.IsDefined(Channel) ||
            (Channel == UpdateReleaseChannel.Beta && !BetaChannelOptIn) ||
            PrimaryAcceptedReleaseSequence < 0 ||
            BetaAcceptedReleaseSequence < 0 ||
            ConsecutiveFailures < 0)
        {
            throw new InvalidDataException("The local update client state is invalid.");
        }

        _ = GetInstallSeed();
        UpdateConditionalRequest.ValidateEntityTag(PrimaryEntityTag);
        UpdateConditionalRequest.ValidateEntityTag(BetaEntityTag);
    }
}

public sealed class FileUpdateClientStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
    };

    private readonly string _path;

    public FileUpdateClientStateStore(string absolutePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(absolutePath);
        if (!Path.IsPathFullyQualified(absolutePath))
        {
            throw new ArgumentException("Update client state path must be absolute.", nameof(absolutePath));
        }

        _path = Path.GetFullPath(absolutePath);
    }

    public async ValueTask<UpdateClientState> LoadOrCreateAsync(
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_path))
        {
            var created = UpdateClientState.CreateNew();
            await SaveAsync(created, cancellationToken).ConfigureAwait(false);
            return created;
        }

        await using var stream = new FileStream(
            _path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length is <= 0 or > 16 * 1024)
        {
            throw new InvalidDataException("The local update client state size is invalid.");
        }

        var state = await JsonSerializer.DeserializeAsync<UpdateClientState>(
            stream,
            JsonOptions,
            cancellationToken).ConfigureAwait(false) ??
            throw new InvalidDataException("The local update client state is empty.");
        state.Validate();
        return state;
    }

    public async ValueTask SaveAsync(
        UpdateClientState state,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        state.Validate();
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporaryPath = _path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    state,
                    JsonOptions,
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}

public sealed record UpdateRolloutDecision(
    bool IsEligible,
    int ActiveBasisPoints,
    DateTimeOffset? ReevaluateAtUtc);

public static class UpdateRolloutGate
{
    public static UpdateRolloutDecision Evaluate(
        VerifiedUpdateManifest manifest,
        UpdateClientState state,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(state);
        state.Validate();
        if (!string.Equals(
            manifest.Channel,
            UpdateReleaseChannels.ToToken(state.Channel),
            StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Update channel and rollout state do not match.");
        }

        var rollout = manifest.Rollout;
        if (nowUtc < rollout.StartUtc)
        {
            return new UpdateRolloutDecision(false, 0, rollout.StartUtc);
        }

        var activeBasisPoints = CalculateActiveBasisPoints(rollout, nowUtc);
        if (activeBasisPoints == 0)
        {
            return new UpdateRolloutDecision(false, 0, NextEvaluation(rollout, nowUtc));
        }

        if (activeBasisPoints == 10_000)
        {
            return new UpdateRolloutDecision(true, 10_000, null);
        }

        var bucket = DeterministicValue(
            state.GetInstallSeed(),
            $"rollout:{manifest.Channel}:{manifest.ReleaseSequence}") % 10_000;
        return new UpdateRolloutDecision(
            bucket < activeBasisPoints,
            activeBasisPoints,
            NextEvaluation(rollout, nowUtc));
    }

    private static int CalculateActiveBasisPoints(
        UpdateRolloutSchedule rollout,
        DateTimeOffset nowUtc)
    {
        if (rollout.StartUtc == rollout.EndUtc || nowUtc >= rollout.EndUtc)
        {
            return rollout.FinalBasisPoints;
        }

        var elapsed = (nowUtc - rollout.StartUtc).TotalSeconds;
        var duration = (rollout.EndUtc - rollout.StartUtc).TotalSeconds;
        var progress = Math.Clamp(elapsed / duration, 0, 1);
        return rollout.InitialBasisPoints + (int)Math.Floor(
            (rollout.FinalBasisPoints - rollout.InitialBasisPoints) * progress);
    }

    private static DateTimeOffset? NextEvaluation(
        UpdateRolloutSchedule rollout,
        DateTimeOffset nowUtc) =>
        nowUtc < rollout.EndUtc ? nowUtc.AddHours(1) : null;

    internal static uint DeterministicValue(byte[] seed, string purpose)
    {
        ArgumentNullException.ThrowIfNull(seed);
        ArgumentException.ThrowIfNullOrWhiteSpace(purpose);
        using var hmac = new HMACSHA256(seed);
        var digest = hmac.ComputeHash(Encoding.UTF8.GetBytes(purpose));
        return BitConverter.ToUInt32(digest, 0);
    }
}

public static class UpdateCheckSchedule
{
    public static TimeSpan GetInitialDelay(UpdateClientState state) =>
        Jitter(state, "initial", TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(15));

    public static TimeSpan GetRegularDelay(UpdateClientState state, long completedCheckNumber) =>
        Jitter(
            state,
            $"regular:{completedCheckNumber}",
            TimeSpan.FromHours(20),
            TimeSpan.FromHours(28));

    public static TimeSpan GetFailureDelay(UpdateClientState state, int consecutiveFailures)
    {
        if (consecutiveFailures <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(consecutiveFailures));
        }

        var exponent = Math.Min(consecutiveFailures - 1, 7);
        var centerMinutes = Math.Min(15 * (1 << exponent), 12 * 60);
        return Jitter(
            state,
            $"failure:{consecutiveFailures}",
            TimeSpan.FromMinutes(centerMinutes * 0.75),
            TimeSpan.FromMinutes(centerMinutes * 1.25));
    }

    private static TimeSpan Jitter(
        UpdateClientState state,
        string purpose,
        TimeSpan minimum,
        TimeSpan maximum)
    {
        ArgumentNullException.ThrowIfNull(state);
        state.Validate();
        var value = UpdateRolloutGate.DeterministicValue(state.GetInstallSeed(), purpose);
        var ratio = value / (double)uint.MaxValue;
        return minimum + TimeSpan.FromTicks((long)((maximum - minimum).Ticks * ratio));
    }
}

public static class UpdateConditionalRequest
{
    public static void ApplyEntityTag(HttpRequestMessage request, string? entityTag)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateEntityTag(entityTag);
        if (entityTag is not null)
        {
            request.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Parse(entityTag));
        }
    }

    public static string? ReadEntityTag(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);
        var value = response.Headers.ETag?.ToString();
        ValidateEntityTag(value);
        return value;
    }

    internal static void ValidateEntityTag(string? entityTag)
    {
        if (entityTag is null)
        {
            return;
        }

        if (entityTag.Length is 0 or > 256 ||
            !EntityTagHeaderValue.TryParse(entityTag, out _))
        {
            throw new InvalidDataException("The update feed entity tag is invalid.");
        }
    }
}

public enum UpdatePublisherTrust
{
    VerificationUnavailable = 0,
    TrustedPublisher = 1,
    Unsigned = 2,
    InvalidSignature = 3,
    UnexpectedPublisher = 4,
}

public enum UpdateApplyDisposition
{
    Blocked = 0,
    RequiresDeliberateConfirmation = 1,
    UserApprovedOnly = 2,
    AutomaticAllowed = 3,
}

public sealed record UpdateApplyDecision(
    UpdateApplyDisposition Disposition,
    string MessageKey);

public static class UpdateApplyPolicy
{
    public const string UnsignedBetaChannelDisclosureKey =
        "warning.update.unsigned_beta_channel_windows";

    public static UpdateApplyDecision Evaluate(
        UpdateReleaseChannel channel,
        UpdatePublisherTrust publisherTrust,
        UpdatePreference preference,
        bool deliberateUnsignedBetaConfirmation)
    {
        if (!Enum.IsDefined(channel) ||
            !Enum.IsDefined(publisherTrust) ||
            !Enum.IsDefined(preference))
        {
            throw new ArgumentOutOfRangeException();
        }

        if (publisherTrust is UpdatePublisherTrust.InvalidSignature or
            UpdatePublisherTrust.UnexpectedPublisher)
        {
            return new UpdateApplyDecision(
                UpdateApplyDisposition.Blocked,
                "error.update.publisher_invalid");
        }

        if (publisherTrust == UpdatePublisherTrust.VerificationUnavailable)
        {
            return new UpdateApplyDecision(
                UpdateApplyDisposition.Blocked,
                "error.update.publisher_verification_unavailable");
        }

        if (publisherTrust == UpdatePublisherTrust.Unsigned)
        {
            if (channel == UpdateReleaseChannel.Primary)
            {
                return new UpdateApplyDecision(
                    UpdateApplyDisposition.Blocked,
                    "error.update.primary_requires_trusted_publisher");
            }

            return deliberateUnsignedBetaConfirmation
                ? new UpdateApplyDecision(
                    UpdateApplyDisposition.UserApprovedOnly,
                    "status.update.unsigned_beta_confirmed")
                : new UpdateApplyDecision(
                    UpdateApplyDisposition.RequiresDeliberateConfirmation,
                    UnsignedBetaChannelDisclosureKey);
        }

        return preference == UpdatePreference.Automatic
            ? new UpdateApplyDecision(
                UpdateApplyDisposition.AutomaticAllowed,
                "status.update.trusted_automatic_allowed")
            : new UpdateApplyDecision(
                UpdateApplyDisposition.UserApprovedOnly,
                "status.update.trusted_user_approval_required");
    }
}
