using System.Collections.ObjectModel;
using System.Text.RegularExpressions;

namespace OrbitNavigator.Contracts.Common;

public enum ControllerResultKind
{
    Error = 0,
    Success = 1,
}

public enum ControllerErrorCode
{
    InvalidRequest = 0,
    NotFound = 1,
    Conflict = 2,
    NotSupported = 3,
    PolicyDenied = 4,
    Unavailable = 5,
    Expired = 6,
    AlreadyHandled = 7,
    Cancelled = 8,
    IntegrityFailure = 9,
    StaleClient = 10,
    InternalFailure = 11,
}

public enum ControllerMessageArgumentKey
{
    ItemCount = 0,
    MaximumCount = 1,
    Capability = 2,
    Scope = 3,
    Setting = 4,
    Reason = 5,
    RetryAfterSeconds = 6,
}

public sealed class ControllerMessageArgument
{
    private static readonly Regex SymbolPattern = new(
        "^[A-Za-z0-9](?:[A-Za-z0-9_.-]{0,63})$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private ControllerMessageArgument(ControllerMessageArgumentKey key, string value)
    {
        Key = key;
        Value = value;
    }

    public ControllerMessageArgumentKey Key { get; }

    public string Value { get; }

    public static ControllerMessageArgument Count(
        ControllerMessageArgumentKey key,
        int value)
    {
        if (key is not (ControllerMessageArgumentKey.ItemCount or
            ControllerMessageArgumentKey.MaximumCount or
            ControllerMessageArgumentKey.RetryAfterSeconds))
        {
            throw new ArgumentOutOfRangeException(nameof(key));
        }

        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }

        return new ControllerMessageArgument(
            key,
            value.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    public static ControllerMessageArgument Symbol(
        ControllerMessageArgumentKey key,
        string value)
    {
        if (key is ControllerMessageArgumentKey.ItemCount or
            ControllerMessageArgumentKey.MaximumCount or
            ControllerMessageArgumentKey.RetryAfterSeconds)
        {
            throw new ArgumentOutOfRangeException(nameof(key));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (!SymbolPattern.IsMatch(value))
        {
            throw new ArgumentException(
                "Controller message symbols must be restricted non-sensitive tokens.",
                nameof(value));
        }

        return new ControllerMessageArgument(key, value);
    }
}

public sealed class ControllerError
{
    private static readonly Regex MessageKeyPattern = new(
        "^[a-z0-9]+(?:[._-][a-z0-9]+)*$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private ControllerError(
        ControllerErrorCode code,
        string messageKey,
        IReadOnlyList<ControllerMessageArgument> formattingArguments,
        bool isRetryable)
    {
        Code = code;
        MessageKey = messageKey;
        FormattingArguments = formattingArguments;
        IsRetryable = isRetryable;
    }

    public ControllerErrorCode Code { get; }

    public string MessageKey { get; }

    public IReadOnlyList<ControllerMessageArgument> FormattingArguments { get; }

    public bool IsRetryable { get; }

    public static ControllerError Create(
        ControllerErrorCode code,
        string messageKey,
        IEnumerable<ControllerMessageArgument>? formattingArguments = null,
        bool isRetryable = false)
    {
        if (!Enum.IsDefined(code))
        {
            throw new ArgumentOutOfRangeException(nameof(code));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(messageKey);
        if (!MessageKeyPattern.IsMatch(messageKey))
        {
            throw new ArgumentException(
                "Controller error message keys must be restricted localization keys.",
                nameof(messageKey));
        }

        var arguments = formattingArguments?.ToArray() ?? [];
        if (arguments.Any(argument => argument is null))
        {
            throw new ArgumentException("Formatting arguments cannot contain null.", nameof(formattingArguments));
        }

        if (arguments.Select(argument => argument.Key).Distinct().Count() != arguments.Length)
        {
            throw new ArgumentException("Formatting argument keys must be unique.", nameof(formattingArguments));
        }

        return new ControllerError(
            code,
            messageKey,
            new ReadOnlyCollection<ControllerMessageArgument>(arguments),
            isRetryable);
    }
}

public sealed class ControllerResult
{
    private ControllerResult(ControllerResultKind kind, ControllerError? error)
    {
        Kind = kind;
        Error = error;
    }

    public ControllerResultKind Kind { get; }

    public ControllerError? Error { get; }

    public bool IsSuccess => Kind == ControllerResultKind.Success;

    public static ControllerResult Success() =>
        new(ControllerResultKind.Success, null);

    public static ControllerResult Failure(ControllerError error) =>
        new(ControllerResultKind.Error, error ?? throw new ArgumentNullException(nameof(error)));
}

public sealed class ControllerResult<T>
    where T : class
{
    private ControllerResult(ControllerResultKind kind, T? value, ControllerError? error)
    {
        Kind = kind;
        Value = value;
        Error = error;
    }

    public ControllerResultKind Kind { get; }

    public T? Value { get; }

    public ControllerError? Error { get; }

    public bool IsSuccess => Kind == ControllerResultKind.Success;

    public static ControllerResult<T> Success(T value) =>
        new(
            ControllerResultKind.Success,
            value ?? throw new ArgumentNullException(nameof(value)),
            null);

    public static ControllerResult<T> Failure(ControllerError error) =>
        new(
            ControllerResultKind.Error,
            null,
            error ?? throw new ArgumentNullException(nameof(error)));
}

