using OrbitNavigator.Contracts.Common;
using Xunit;

namespace OrbitNavigator.Contracts.Tests.Common;

public sealed class ControllerResultTests
{
    [Fact]
    public void Success_HasValueAndNoError()
    {
        var result = ControllerResult<string>.Success("value");

        Assert.True(result.IsSuccess);
        Assert.Equal(ControllerResultKind.Success, result.Kind);
        Assert.Equal("value", result.Value);
        Assert.Null(result.Error);
    }

    [Fact]
    public void Failure_HasErrorAndNoValue()
    {
        var error = ControllerError.Create(
            ControllerErrorCode.PolicyDenied,
            "error.policy.denied");

        var result = ControllerResult<string>.Failure(error);

        Assert.False(result.IsSuccess);
        Assert.Equal(ControllerResultKind.Error, result.Kind);
        Assert.Null(result.Value);
        Assert.Same(error, result.Error);
    }

    [Theory]
    [InlineData("Contains spaces")]
    [InlineData("https://example.test")]
    [InlineData("error/{origin}")]
    [InlineData("")]
    public void Error_RejectsUnsafeMessageKeys(string messageKey)
    {
        Assert.ThrowsAny<ArgumentException>(() =>
            ControllerError.Create(ControllerErrorCode.InvalidRequest, messageKey));
    }

    [Fact]
    public void SymbolArgument_RejectsSensitiveFreeText()
    {
        Assert.Throws<ArgumentException>(() =>
            ControllerMessageArgument.Symbol(
                ControllerMessageArgumentKey.Reason,
                "https://private.example/path"));
    }

    [Fact]
    public void Error_RejectsDuplicateFormattingKeys()
    {
        var arguments = new[]
        {
            ControllerMessageArgument.Count(ControllerMessageArgumentKey.ItemCount, 1),
            ControllerMessageArgument.Count(ControllerMessageArgumentKey.ItemCount, 2),
        };

        Assert.Throws<ArgumentException>(() =>
            ControllerError.Create(
                ControllerErrorCode.InvalidRequest,
                "error.items.invalid",
                arguments));
    }
}

