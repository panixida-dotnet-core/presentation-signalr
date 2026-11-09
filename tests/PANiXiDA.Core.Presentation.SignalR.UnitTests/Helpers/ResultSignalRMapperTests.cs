using Microsoft.AspNetCore.SignalR;

using PANiXiDA.Core.Presentation.SignalR.Helpers;
using PANiXiDA.Core.ResultPattern;

namespace PANiXiDA.Core.Presentation.SignalR.UnitTests.Helpers;

public sealed class ResultSignalRMapperTests
{
    [Fact(DisplayName = "Successful results without a value complete without an exception")]
    public void ThrowIfFailure_ShouldAcceptSuccess()
    {
        Should.NotThrow(() => Result.Success().ThrowIfFailure());
    }

    [Theory(DisplayName = "Successful generic results return their original values")]
    [InlineData(0)]
    [InlineData(42)]
    public void GetValueOrThrow_ShouldPreserveValues(int value)
    {
        Result.Success(value).GetValueOrThrow().ShouldBe(value);
    }

    [Fact(DisplayName = "Successful results preserve false and nullable reference and value types")]
    public void GetValueOrThrow_ShouldPreserveFalseAndNull()
    {
        Result.Success(false).GetValueOrThrow().ShouldBeFalse();
        Result.Success<string?>(null).GetValueOrThrow().ShouldBeNull();
        Result.Success<int?>(null).GetValueOrThrow().ShouldBeNull();
    }

    [Theory(DisplayName = "Expected failures become standard HubExceptions containing only the public message")]
    [InlineData(ErrorType.Validation)]
    [InlineData(ErrorType.NotFound)]
    [InlineData(ErrorType.Conflict)]
    [InlineData(ErrorType.Unauthorized)]
    [InlineData(ErrorType.Forbidden)]
    [InlineData(ErrorType.Failure)]
    public void ThrowIfFailure_ShouldMapExpectedErrors(ErrorType type)
    {
        var error = new Error("Expected failure", type).WithMetadata("secret", "private-value");

        var exception = Should.Throw<HubException>(() => Result.Failure(error).ThrowIfFailure());

        exception.GetType().ShouldBe(typeof(HubException));
        exception.Message.ShouldBe("Expected failure");
        exception.InnerException.ShouldBeNull();
        exception.Data.Count.ShouldBe(0);
    }

    [Theory(DisplayName = "Unexpected and unknown error categories do not expose internal details")]
    [InlineData(ErrorType.Unexpected)]
    [InlineData((ErrorType)123)]
    public void ThrowIfFailure_ShouldSanitizeUnexpectedErrors(ErrorType type)
    {
        var result = Result.Failure(new Error("Internal database detail", type));

        var exception = Should.Throw<HubException>(() => result.ThrowIfFailure());

        exception.Message.ShouldBe("An unexpected error occurred.");
    }

    [Fact(DisplayName = "Multiple errors use the first error without exposing additional messages or field metadata")]
    public void ThrowIfFailure_ShouldUseFirstError()
    {
        var result = Result.Failure([
            Error.Validation("Invalid name").WithField("Name"),
            Error.Unexpected("Internal secret")]);

        var exception = Should.Throw<HubException>(() => result.ThrowIfFailure());

        exception.Message.ShouldBe("Invalid name");
        exception.Data.Count.ShouldBe(0);
    }

    [Fact(DisplayName = "Failed generic results throw HubException before accessing their throwing Value property")]
    public void GetValueOrThrow_ShouldMapFailure()
    {
        var reference = Result.Failure<string>(Error.NotFound("Missing"));
        var number = Result.Failure<int>(Error.Unexpected("Internal secret"));

        Should.Throw<HubException>(() => reference.GetValueOrThrow()).Message.ShouldBe("Missing");
        Should.Throw<HubException>(() => number.GetValueOrThrow()).Message.ShouldBe("An unexpected error occurred.");
    }

    [Fact(DisplayName = "Both result helpers reject null arguments")]
    public void ResultHelpers_ShouldRejectNull()
    {
        Should.Throw<ArgumentNullException>(() => ((Result)null!).ThrowIfFailure()).ParamName.ShouldBe("result");
        Should.Throw<ArgumentNullException>(() => ((Result<string>)null!).GetValueOrThrow()).ParamName.ShouldBe("result");
    }
}
