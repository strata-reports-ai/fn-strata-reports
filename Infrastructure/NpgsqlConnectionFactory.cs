using System.Data;
using Npgsql;

namespace StrataReports.Functions.Infrastructure;

public interface IDbConnectionFactory
{
    Task<IDbConnection> OpenAsync(Guid tenantId, CancellationToken ct = default);
}

public class NpgsqlConnectionFactory(string connectionString) : IDbConnectionFactory
{
    public async Task<IDbConnection> OpenAsync(Guid tenantId, CancellationToken ct = default)
    {
        var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SET LOCAL app.current_tenant_id = '{tenantId}'";
        await cmd.ExecuteNonQueryAsync(ct);
        return conn;
    }
}
