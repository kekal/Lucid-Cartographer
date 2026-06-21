using System.Reflection;

namespace LucidCartographer.Endpoints;

/// <summary>
/// Serves operator documentation as embedded resources. The OSRM guide is embedded
/// rather than static because .md is an unknown MIME type and Docker builds exclude *.md files.
/// Served as text/plain for inline markdown rendering.
/// </summary>
public static class DocsEndpoints
{
    // Resolve by suffix to tolerate namespace/rename changes without breaking the resource lookup.
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
