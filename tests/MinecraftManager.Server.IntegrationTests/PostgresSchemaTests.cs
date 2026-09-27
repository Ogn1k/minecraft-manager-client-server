using Testcontainers.PostgreSql;
using Microsoft.EntityFrameworkCore;
using MinecraftManager.Server.Infrastructure.Persistence;
using MinecraftManager.Server.Domain.Entities;

namespace MinecraftManager.Server.IntegrationTests;

public sealed class PostgresSchemaTests
{
    [Fact]
    public async Task PostgreSql_container_can_start_when_Docker_is_available()
    {
        if (Environment.GetEnvironmentVariable("RUN_CONTAINER_TESTS") != "1") return;
        await using var database = new PostgreSqlBuilder("postgres:17-alpine").Build();
        await database.StartAsync();
        Assert.Contains("Host=", database.GetConnectionString(), StringComparison.Ordinal);
        var options = new DbContextOptionsBuilder<ServerDbContext>().UseNpgsql(database.GetConnectionString()).Options;
        await using var db = new ServerDbContext(options);
        await db.Database.MigrateAsync();
        db.Add(new Pack { Slug = "test", NormalizedSlug = "test", DisplayName = "Test" });
        await db.SaveChangesAsync();
        Assert.Equal(1, await db.Set<Pack>().CountAsync());
    }
}
