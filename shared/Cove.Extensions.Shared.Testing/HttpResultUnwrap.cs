using Microsoft.AspNetCore.Http;
using Xunit;

namespace Cove.Extensions.Shared.Testing;

/// <summary>Reads the status and the value a handler's result carries.</summary>
/// <remarks>
/// A <c>Results&lt;…&gt;</c> union implements neither <see cref="IStatusCodeHttpResult"/> nor
/// <see cref="IValueHttpResult"/> yet converts implicitly to <see cref="IResult"/>, so widening a
/// handler's return type compiles at every call site and then throws inside any assertion that reads a
/// status or a value off the result. Unwrapping through <see cref="INestedHttpResult"/> covers every
/// union arity.
/// </remarks>
public static class HttpResultUnwrap
{
    /// <summary>
    /// Returns the inner result when <paramref name="result"/> is a <c>Results&lt;…&gt;</c> union, and
    /// <paramref name="result"/> itself otherwise.
    /// </summary>
    public static IResult Unwrap(IResult result)
        => result is INestedHttpResult nested ? nested.Result : result;

    /// <summary>The status <paramref name="result"/> writes, or <c>0</c> when it declares none.</summary>
    public static int StatusOf(IResult result)
        => Assert.IsType<IStatusCodeHttpResult>(Unwrap(result), exactMatch: false).StatusCode ?? 0;

    /// <summary>
    /// The body <paramref name="result"/> carries, asserted to be a <typeparamref name="T"/>.
    /// </summary>
    /// <remarks>
    /// Asserts the body's own type rather than the result's: a refusal arm of the same union is an
    /// <see cref="IValueHttpResult"/> too, so a non-null check on the result would pass for it.
    /// </remarks>
    public static T ValueOf<T>(IResult result)
        => Assert.IsType<T>(Assert.IsType<IValueHttpResult>(Unwrap(result), exactMatch: false).Value);
}
