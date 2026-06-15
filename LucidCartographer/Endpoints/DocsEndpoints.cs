using System.Reflection;

namespace LucidCartographer.Endpoints;

/// <summary>
/// Serves the in-app operator documentation that the UI links to — currently the
/// "How to enable OSRM" guide reached from the Trip View Mock-estimate note
/// (<c>UiStrings.TripMockEstimateOsrmHref</c> = <c>docs/osrm.md</c>, opened in a new
/// tab). The guide ships as an <b>embedded resource</b> (see the app .csproj) rather
/// than a wwwroot static file on purpose:
///   1. <c>UseStaticFiles</c> will not serve <c>.md</c> — it is an unknown content type,
///      so a wwwroot copy would 404.
///   2. The Docker build excludes <c>*.md</c> (.dockerignore), so a loose file would be
///      stripped from the image. An embedded resource is compiled into the assembly and
///      is always present at runtime, on every host.
///
/// Source of truth for the prose is <c>docs/osrm.md</c> at the repo root (the maintainer
/// docs set); <c>Endpoints/Docs/osrm.md</c> is the shipped copy embedded here — keep the
/// two in sync when the guide changes.
///
/// Served as <c>text/plain</c> so browsers render the markdown inline (the link is a
/// plain reference doc, not a rendered page). Anonymous: the guide carries no secrets,
/// and the request rides the user's existing session anyway.
/// </summary>
public static class DocsEndpoints
{
    // The embedded resource logical name is "{RootNamespace}.Endpoints.Docs.osrm.md".
    // Resolve it by suffix so a namespace/rename can't silently break the lookup.
    private static readonly Lazy<string?> OsrmGuide = new(() =>
    {
        var asm = typeof(DocsEndpoints).Assembly;
        var name = Array.Find(
            asm.GetManifestResourceNames(),
            n => n.EndsWith("Docs.osrm.md", StringComparison.Ordinal));
        if (name is null)
        {
            return null;
        }

        using var stream = asm.GetManifestResourceStream(name);
        if (stream is null)
        {
            return null;
        }

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    public static IEndpointRouteBuilder MapDocsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/docs/osrm.md", () =>
                OsrmGuide.Value is { } guide
                    ? Results.Text(guide, "text/plain; charset=utf-8")
                    : Results.NotFound())
            .AllowAnonymous();

        return endpoints;
    }
}
