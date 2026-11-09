using System.Reflection;

using Microsoft.AspNetCore.SignalR;

using PANiXiDA.Core.Presentation.SignalR.Helpers;
using PANiXiDA.Core.Presentation.SignalR.Hubs;
using PANiXiDA.Core.ResultPattern;

namespace PANiXiDA.Core.Presentation.SignalR.UnitTests.Hubs;

public sealed class HubExceptionFilterTests
{
    [Fact(DisplayName = "Reflection dispatch wrappers retain the original mapped HubException and its stack trace")]
    public async Task InvokeMethodAsync_ShouldUnwrapHubException()
    {
        var expected = Should.Throw<HubException>(() => Result.Failure(Error.NotFound("Missing")).ThrowIfFailure());
        var wrapped = new TargetInvocationException(expected);
        var filter = new HubExceptionFilter();

        var actual = await Should.ThrowAsync<HubException>(() =>
            filter.InvokeMethodAsync(null!, _ => ValueTask.FromException<object?>(wrapped)).AsTask());

        actual.ShouldBeSameAs(expected);
        actual.Message.ShouldBe("Missing");
        actual.StackTrace.ShouldNotBeNull().ShouldContain(nameof(ResultSignalRMapper.ThrowIfFailure));
    }

    [Theory(DisplayName = "Other exceptions propagate unchanged regardless of reflection wrapping")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvokeMethodAsync_ShouldPreserveOtherExceptions(bool wrapped)
    {
        Exception expected = new InvalidOperationException("Internal failure");
        if (wrapped) { expected = new TargetInvocationException(expected); }
        var filter = new HubExceptionFilter();

        var actual = await Should.ThrowAsync<Exception>(() =>
            filter.InvokeMethodAsync(null!, _ => ValueTask.FromException<object?>(expected)).AsTask());

        actual.ShouldBeSameAs(expected);
    }

    [Fact(DisplayName = "Successful invocations pass their original value through unchanged")]
    public async Task InvokeMethodAsync_ShouldPreserveValue()
    {
        var expected = new object();
        var filter = new HubExceptionFilter();

        var actual = await filter.InvokeMethodAsync(null!, _ => ValueTask.FromResult<object?>(expected));

        actual.ShouldBeSameAs(expected);
    }
}
