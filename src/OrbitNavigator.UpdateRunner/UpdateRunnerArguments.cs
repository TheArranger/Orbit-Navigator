namespace OrbitNavigator.UpdateRunner;

public sealed record UpdateRunnerArguments(
    string PackagePath,
    string Sha256,
    long SizeBytes,
    int? WaitForProcessId)
{
    public static bool TryParse(string[] args, out UpdateRunnerArguments? value)
    {
        value = null;
        string? package = null;
        string? sha256 = null;
        long? size = null;
        int? waitPid = null;
        for (var index = 0; index < args.Length; index++)
        {
            if (index + 1 >= args.Length)
            {
                return false;
            }

            var argument = args[index];
            var content = args[++index];
            switch (argument)
            {
                case "--package":
                    package = content;
                    break;
                case "--sha256":
                    sha256 = content;
                    break;
                case "--size":
                    if (!long.TryParse(content, out var parsedSize) || parsedSize <= 0)
                    {
                        return false;
                    }
                    size = parsedSize;
                    break;
                case "--wait-pid":
                    if (!int.TryParse(content, out var parsedPid) || parsedPid <= 0)
                    {
                        return false;
                    }
                    waitPid = parsedPid;
                    break;
                default:
                    return false;
            }
        }

        if (package is null || sha256 is null || size is null)
        {
            return false;
        }

        value = new UpdateRunnerArguments(package, sha256, size.Value, waitPid);
        return true;
    }
}
