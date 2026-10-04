using TestDetta.Core.Licensing;

namespace TestDetta.LicenseService;

/// <summary>
/// <c>GET /v1/licenses/{id}</c> with <c>Authorization: TestDetta &lt;license&gt;</c>: the current signed
/// license for <c>id</c> (docs/design/licensing.md, "Refresh"). The presented license proves the
/// caller holds that license: it must be genuine and for the same id, though it may have expired (a
/// renewal is exactly what an expired license asks for). Unknown, revoked and unproven requests all
/// get the same 404, so ids cannot be probed. Nothing of the request is logged.
/// </summary>
public static class RefreshEndpoint
{
    /// <summary>Scheme of the Authorization header.</summary>
    private const string Scheme = "TestDetta ";

    /// <summary>Handles one refresh.</summary>
    /// <param name="id">License id from the path.</param>
    /// <param name="request">The request, for its Authorization header.</param>
    /// <param name="store">License records.</param>
    /// <param name="issuer">Signs the current license.</param>
    /// <param name="trusted">Keys a presented license may be signed with.</param>
    /// <param name="cancellationToken">Request aborted.</param>
    public static async Task<IResult> HandleAsync(
        string id,
        HttpRequest request,
        LicenseStore store,
        LicenseIssuer issuer,
        TrustedKeys trusted,
        CancellationToken cancellationToken)
    {
        var header = request.Headers.Authorization.ToString();
        if (!header.StartsWith(Scheme, StringComparison.Ordinal)
            || License.Verify(header[Scheme.Length..], trusted.Keys, out _) is not { } presented
            || presented.Id != id
            || await store.GetAsync(id, cancellationToken) is not { Revoked: false } record)
        {
            return Results.NotFound();
        }

        request.HttpContext.Response.Headers.CacheControl = "no-store";
        return Results.Text(issuer.Issue(record), "text/plain");
    }
}
