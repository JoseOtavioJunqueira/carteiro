using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Carteiro.Data;

/// <summary>
/// Used only by the <c>dotnet ef</c> tooling. Generating a migration needs the model, not a live
/// database, so the connection string here is a placeholder unless one is passed on the command line.
/// </summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<CarteiroDbContext>
{
    public CarteiroDbContext CreateDbContext(string[] args)
    {
        var connectionString = args.Length > 0
            ? args[0]
            : "Host=localhost;Database=carteiro;Username=carteiro;Password=carteiro";

        var options = new DbContextOptionsBuilder<CarteiroDbContext>()
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention()
            .Options;

        return new CarteiroDbContext(options);
    }
}
