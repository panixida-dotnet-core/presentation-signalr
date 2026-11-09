using PANiXiDA.Core.Presentation.SignalR.Helpers;
using PANiXiDA.Core.Presentation.SignalR.Results;
using PANiXiDA.Core.ResultPattern;

namespace PANiXiDA.Core.Presentation.SignalR.UnitTests.Helpers;

public sealed class ResultSignalRMapperTests
{
    [Fact(DisplayName = "Successful results without a value have no errors")]
    public void ToSignalRResult_ShouldMapSuccess()
    {
        var response = Result.Success().ToSignalRResult();

        response.IsSuccess.ShouldBeTrue();
        response.Errors.ShouldBeEmpty();
    }

    [Theory(DisplayName = "Successful generic results preserve default values")]
    [InlineData(0)]
    [InlineData(42)]
    public void ToSignalRResult_ShouldPreserveValues(int value)
    {
        var response = Result.Success(value).ToSignalRResult();

        response.IsSuccess.ShouldBeTrue();
        response.Value.ShouldBe(value);
        response.Errors.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Successful results preserve false and nullable reference and value types")]
    public void ToSignalRResult_ShouldPreserveFalseAndNull()
    {
        var boolean = Result.Success(false).ToSignalRResult();
        var nullable = Result.Success<string?>(null).ToSignalRResult();
        var nullableNumber = Result.Success<int?>(null).ToSignalRResult();

        boolean.IsSuccess.ShouldBeTrue();
        boolean.Value.ShouldBeFalse();
        nullable.IsSuccess.ShouldBeTrue();
        nullable.Value.ShouldBeNull();
        nullableNumber.IsSuccess.ShouldBeTrue();
        nullableNumber.Value.ShouldBeNull();
    }

    [Theory(DisplayName = "Expected errors preserve their category and message without exposing metadata")]
    [InlineData(ErrorType.NotFound)]
    [InlineData(ErrorType.Conflict)]
    [InlineData(ErrorType.Unauthorized)]
    [InlineData(ErrorType.Forbidden)]
    [InlineData(ErrorType.Failure)]
    public void ToSignalRResult_ShouldMapExpectedErrors(ErrorType type)
    {
        var error = new Error("Expected failure", type).WithMetadata("secret", "private-value");

        var response = Result.Failure(error).ToSignalRResult();

        response.IsSuccess.ShouldBeFalse();
        response.Errors.ShouldHaveSingleItem().ShouldBe(new SignalRError(type.ToString(), "Expected failure", null));
    }

    [Theory(DisplayName = "Unexpected and unknown error categories do not expose internal messages")]
    [InlineData(ErrorType.Unexpected)]
    [InlineData((ErrorType)123)]
    public void ToSignalRResult_ShouldSanitizeUnexpectedErrors(ErrorType type)
    {
        var response = Result.Failure(new Error("Internal database detail", type)).ToSignalRResult();

        response.Errors.ShouldHaveSingleItem().ShouldBe(new SignalRError("Unexpected", "An unexpected error occurred.", null));
    }

    [Theory(DisplayName = "Validation errors expose a valid field name or use the general fallback")]
    [InlineData(null, "general")]
    [InlineData("", "general")]
    [InlineData(" ", "general")]
    [InlineData("BuildId", "BuildId")]
    [InlineData(42, "general")]
    public void ToSignalRResult_ShouldMapValidationFields(object? field, string expected)
    {
        var error = Error.Validation("Invalid value");
        if (field is not null) { error = error.WithMetadata(Error.FieldMetadataKey, field); }

        var response = Result.Failure(error).ToSignalRResult();

        response.Errors.ShouldHaveSingleItem().ShouldBe(new SignalRError("Validation", "Invalid value", expected));
    }

    [Fact(DisplayName = "Multiple errors retain order and are exposed as a read-only snapshot")]
    public void ToSignalRResult_ShouldPreserveAllErrors()
    {
        var response = Result.Failure([
            Error.Validation("First").WithField("Name"),
            Error.Conflict("Second"),
            Error.Validation("Third").WithField("Name")]).ToSignalRResult();

        response.Errors.Select(error => error.Message).ShouldBe(["First", "Second", "Third"]);
        var errors = response.Errors.ShouldBeAssignableTo<IList<SignalRError>>();
        errors.IsReadOnly.ShouldBeTrue();
        Should.Throw<NotSupportedException>(() => errors[0] = new SignalRError("Failure", "Changed", null));
    }

    [Fact(DisplayName = "Failed generic results can be mapped without accessing their throwing Value property")]
    public void ToSignalRResult_ShouldMapFailedGenericResults()
    {
        var reference = Result.Failure<string>(Error.NotFound("Missing")).ToSignalRResult();
        var number = Result.Failure<int>(Error.NotFound("Missing")).ToSignalRResult();

        reference.IsSuccess.ShouldBeFalse();
        reference.Value.ShouldBeNull();
        reference.Errors.ShouldHaveSingleItem().Type.ShouldBe("NotFound");
        number.IsSuccess.ShouldBeFalse();
        number.Value.ShouldBe(0);
        number.Errors.ShouldBe(reference.Errors);
    }

    [Fact(DisplayName = "Null results are rejected by both mapper overloads")]
    public void ToSignalRResult_ShouldRejectNull()
    {
        Should.Throw<ArgumentNullException>(() => ((Result)null!).ToSignalRResult()).ParamName.ShouldBe("result");
        Should.Throw<ArgumentNullException>(() => ((Result<string>)null!).ToSignalRResult()).ParamName.ShouldBe("result");
    }
}
