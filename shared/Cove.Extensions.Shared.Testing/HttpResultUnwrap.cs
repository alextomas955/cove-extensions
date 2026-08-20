using Microsoft.AspNetCore.Http;

namespace Cove.Extensions.Shared.Testing;

/// <summary>Reads the result a <c>Results&lt;…&gt;</c> union actually carries.</summary>
/// <remarks>
/// A union does not implement <see cref="IStatusCodeHttpResult"/> or <see cref="IValueHttpResult"/>,
/// yet it converts implicitly to <see cref="IResult"/> — so widening a handler's declared return type
/// to a union compiles at every call site and then throws inside any assertion that reads a status or
/// a value off the result. Unwrapping through <see cref="INestedHttpResult"/> covers every union
/// arity, so a test needs no switch and no per-arity overload.
/// </remarks>
public static class HttpResultUnwrap
{
    /// <summary>
    /// Returns the inner result when <paramref name="result"/> is a <c>Results&lt;…&gt;</c> union, and
    /// <paramref name="result"/> itself otherwise.
    /// </summary>
    public static IResult Unwrap(IResult result)
        => result is INestedHttpResult nested ? nested.Result : result;
}
