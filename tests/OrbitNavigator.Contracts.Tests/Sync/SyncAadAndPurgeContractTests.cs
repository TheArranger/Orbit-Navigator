using System.Text;
using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Sync;
using Xunit;

namespace OrbitNavigator.Contracts.Tests.Sync;

public sealed class SyncAadAndPurgeContractTests
{
    [Fact]
    public void CanonicalAadBindsEveryRoutingAndFencingIdentity()
    {
        var original = Aad(SyncRecordKind.Purge);
        var encoded = Convert.ToHexString(SyncContractRules.EncodeCanonicalAad(original));
        var mutations = new[]
        {
            original with { ProfileId = new ProfileId(Guid.NewGuid()) },
            original with { DeviceId = new DeviceId(Guid.NewGuid()) },
            original with { KeysetId = new SyncKeysetId(Guid.NewGuid()) },
            original with { KeyEpoch = original.KeyEpoch + 1 },
            original with { RecordKind = SyncRecordKind.Upsert, OperationId = null },
            original with { EnvelopeId = new SyncEnvelopeId(Guid.NewGuid()) },
            original with { Category = SyncDataCategory.Settings },
            original with { EntityId = new SyncEntityId(Guid.NewGuid()) },
            original with { OperationId = new SyncOperationId(Guid.NewGuid()) },
            original with { ClientGeneration = original.ClientGeneration + 1 },
            original with { ClientSequence = original.ClientSequence + 1 },
        };

        Assert.All(mutations, mutation =>
            Assert.NotEqual(encoded, Convert.ToHexString(SyncContractRules.EncodeCanonicalAad(mutation))));

        Assert.False(SyncContractRules.ValidateAad(
            original with { ProtocolVersion = original.ProtocolVersion + 1 }).IsValid);
        Assert.False(SyncContractRules.ValidateAad(
            original with { SchemaVersion = original.SchemaVersion + 1 }).IsValid);
    }

    [Fact]
    public void PurgeMarkerMustExactlyMatchOperationProfileCategoryGenerationAndKeyset()
    {
        var aad = Aad(SyncRecordKind.Purge);
        var marker = Marker(aad);
        Assert.True(SyncContractRules.ValidatePurgeMarker(aad, marker).IsValid);

        var substitutions = new[]
        {
            marker with { OperationId = new SyncOperationId(Guid.NewGuid()) },
            marker with { ProfileId = new ProfileId(Guid.NewGuid()) },
            marker with { Category = SyncDataCategory.Settings },
            marker with { ClientGeneration = marker.ClientGeneration + 1 },
            marker with { KeysetId = new SyncKeysetId(Guid.NewGuid()) },
        };

        Assert.All(substitutions, substitution =>
            Assert.False(SyncContractRules.ValidatePurgeMarker(aad, substitution).IsValid));
    }

    [Fact]
    public void PurgeCommandRepresentsExactlyOneAadCategory()
    {
        var aad = Aad(SyncRecordKind.Purge);
        var command = new EncryptedPurgeCommand(
            aad,
            new byte[SyncProtocol.NonceSizeBytes],
            Encoding.UTF8.GetBytes("encrypted-marker"),
            new byte[SyncProtocol.AuthenticationTagSizeBytes]);

        var validation = SyncContractRules.ValidatePurgeCommand(command);

        Assert.True(validation.IsValid);
        Assert.Equal(aad.Category, command.Aad.Category);
        Assert.DoesNotContain(
            typeof(EncryptedPurgeCommand).GetProperties(),
            property => typeof(IEnumerable<SyncDataCategory>).IsAssignableFrom(property.PropertyType));
    }

    [Fact]
    public void PurgeAadRequiresOperationId()
    {
        var invalid = Aad(SyncRecordKind.Purge) with { OperationId = null };

        var validation = SyncContractRules.ValidateAad(invalid);

        Assert.False(validation.IsValid);
        Assert.Contains(validation.Issues, issue => issue.Code == "aad.operation");
    }

    private static CanonicalSyncAad Aad(SyncRecordKind kind) =>
        new(
            SyncProtocol.CurrentProtocolVersion,
            SyncProtocol.CurrentSchemaVersion,
            new ProfileId(Guid.NewGuid()),
            new DeviceId(Guid.NewGuid()),
            new SyncKeysetId(Guid.NewGuid()),
            4,
            kind,
            new SyncEnvelopeId(Guid.NewGuid()),
            SyncDataCategory.History,
            new SyncEntityId(Guid.NewGuid()),
            kind == SyncRecordKind.Purge ? new SyncOperationId(Guid.NewGuid()) : null,
            9,
            12);

    private static DecryptedPurgeMarker Marker(CanonicalSyncAad aad) =>
        new(
            aad.OperationId!.Value,
            aad.ProfileId,
            aad.Category,
            aad.ClientGeneration,
            aad.KeysetId);
}
