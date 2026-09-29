using Microsoft.AspNetCore.Http;

namespace WhisparrSync.Tests.TestSupport;

internal static class TestRequest
{
    public static DefaultHttpContext From(string origin)
    {
        var at = new Uri(origin);
        var http = new DefaultHttpContext();
        http.Request.Scheme = at.Scheme;
        http.Request.Host = new HostString(at.Authority);
        return http;
    }
}
