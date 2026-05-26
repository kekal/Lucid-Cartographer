using LucidCartographer.Data;
using Microsoft.EntityFrameworkCore;

namespace LucidCartographer.Configuration;

public static class DatabaseServicesExtensions
{
    /// <summary>
    /// Registers AppDbContext factory using SQLite. DB path is resolved with this precedence:
    ///   1. DB_PATH environment variable (simple override for Docker/cloud)
    ///   2. Database:Path from configuration (also honours Database__Path env var)
    ///   3. Default "data/cartographer.db" relative to ContentRootPath
    /// Relative paths are resolved against ContentRootPath so the process does not depend
    /// on the current working directory. The containing directory is created if missing.
    /// (MED-06: OS-independent DB path resolution.)
    /// </summary>
    public static IServiceCollection AddAppDatabase(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        var dbPath = ResolveDbPath(configuration, environment);
        services.AddDbContextFactory<AppDbContext>(options =>
            options.UseSqlite($"Data Source={dbPath}")
                // Register the OpenIddict entity sets on the model so the OAuth
                // frontdoor's clients/tokens/authorizations live in the same DB
                // and are picked up by EF migrations.
                .UseOpenIddict());

        // OpenIddict's managers resolve a request-scoped AppDbContext from DI, but
        // AddDbContextFactory only registers the factory. Add a scoped context
        // sourced from that factory so OAuth requests get a writable per-request
        // context (the container disposes it at scope end).
        services.AddScoped<AppDbContext>(sp =>
            sp.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContext());

        return services;
    }

    /// <summary>
    /// The directory that holds persistent on-disk state (the SQLite DB and the
    /// OAuth signing/encryption keys). Same location the DB resolves to, so a
    /// single mounted volume persists everything. Created if missing.
    /// </summary>
    internal static string ResolveDataDirectory(IConfiguration cfg, IHostEnvironment env)
    {
        var dir = Path.GetDirectoryName(ResolveDbPath(cfg, env));
        if (string.IsNullOrEmpty(dir))
        {
            dir = Path.GetFullPath(Path.Combine(env.ContentRootPath, "data"));
        }
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string ResolveDbPath(IConfiguration cfg, IHostEnvironment env)
    {
        var raw = Environment.GetEnvironmentVariable("DB_PATH");
        if (string.IsNullOrWhiteSpace(raw))
        {
            raw = cfg.GetValue<string>("Database:Path");
        }

        if (string.IsNullOrWhiteSpace(raw))
        {
            raw = Path.Combine("data", "cartographer.db");
        }

        var full = Path.IsPathRooted(raw)
            ? raw
            : Path.GetFullPath(Path.Combine(env.ContentRootPath, raw));

        var dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        return full;
    }
}
