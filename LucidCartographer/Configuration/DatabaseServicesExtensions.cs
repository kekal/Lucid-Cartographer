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
            options.UseSqlite($"Data Source={dbPath}"));
        return services;
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
