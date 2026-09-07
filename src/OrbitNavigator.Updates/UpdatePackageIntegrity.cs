using System.Security.Cryptography;
using System.Text.RegularExpressions;
using OrbitNavigator.Contracts.Common;
using OrbitNavigator.Contracts.Updates;

namespace OrbitNavigator.Updates;

public static partial class UpdatePackageIntegrity
{
    public static async ValueTask<ControllerResult> VerifyAsync(
        StagedUpdatePackage staged,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(staged);
        if (!Path.IsPathFullyQualified(staged.AbsolutePath) ||
            !File.Exists(staged.AbsolutePath) ||
            !Sha256Pattern().IsMatch(staged.Package.Sha256))
        {
            return ControllerResult.Failure(ControllerError.Create(
                ControllerErrorCode.InvalidRequest,
                "error.update.package_invalid"));
        }

        var info = new FileInfo(staged.AbsolutePath);
        if (info.Length != staged.Package.SizeBytes)
        {
            return ControllerResult.Failure(ControllerError.Create(
                ControllerErrorCode.IntegrityFailure,
                "error.update.size_mismatch"));
        }

        await using var stream = new FileStream(
            staged.AbsolutePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        var actual = Convert.ToHexString(hash);
        return CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(staged.Package.Sha256),
            Convert.FromHexString(actual))
            ? ControllerResult.Success()
            : ControllerResult.Failure(ControllerError.Create(
                ControllerErrorCode.IntegrityFailure,
                "error.update.hash_mismatch"));
    }

    [GeneratedRegex("^[A-Fa-f0-9]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Pattern();
}
