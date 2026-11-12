using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.Data.SqlClient;

namespace LogicApp.Tests.Integration.Infrastructure;

/// <summary>
/// Opt-in Azure SQL Edge Testcontainer fixture for Logic Apps using /serviceProviders/sql.
/// Copy into Infrastructure/ and add a PackageReference for Microsoft.Data.SqlClient when needed.
/// Honours SQL_CONNECTION_STRING and SQL_SERVER_CONNECTION_STRING environment-variable escape hatches.
/// </summary>
public sealed class SqlServerFixture : IAsyncDisposable
{
    private const string DefaultSqlEdgeImage = "mcr.microsoft.com/azure-sql-edge:1.0.7";
    private const string DefaultSaPassword = "Pwd-12345-StrongEnough!";

    private static string SqlImage =>
        Environment.GetEnvironmentVariable("SQL_SERVER_IMAGE") is { Length: > 0 } sqlImg
            ? sqlImg
            : Environment.GetEnvironmentVariable("SQL_EDGE_IMAGE") is { Length: > 0 } edgeImg
                ? edgeImg
                : DefaultSqlEdgeImage;

    private IContainer? _container;

    public SqlServerFixture(string databaseName = "IntegrationTestDb")
    {
        DatabaseName = databaseName;
    }

    public string DatabaseName { get; }
    public string Host { get; private set; } = "127.0.0.1";
    public int Port { get; private set; } = 1433;
    public string Username { get; private set; } = "sa";
    public string Password { get; private set; } = DefaultSaPassword;
    public bool IsInitialized { get; private set; }

    public string ConnectionString
    {
        get
        {
            var escape = Environment.GetEnvironmentVariable("SQL_CONNECTION_STRING");
            if (!string.IsNullOrWhiteSpace(escape))
            {
                return escape;
            }

            return $"Server={Host},{Port};Database={DatabaseName};User Id={Username};Password={Password};TrustServerCertificate=True;Encrypt=False;";
        }
    }

    public string MasterConnectionString
    {
        get
        {
            var masterEscape = Environment.GetEnvironmentVariable("SQL_SERVER_CONNECTION_STRING");
            if (!string.IsNullOrWhiteSpace(masterEscape))
            {
                return masterEscape;
            }

            var escape = Environment.GetEnvironmentVariable("SQL_CONNECTION_STRING");
            if (!string.IsNullOrWhiteSpace(escape))
            {
                var builder = new SqlConnectionStringBuilder(escape) { InitialCatalog = "master" };
                return builder.ConnectionString;
            }

            return $"Server={Host},{Port};Database=master;User Id={Username};Password={Password};TrustServerCertificate=True;Encrypt=False;";
        }
    }

    public async Task InitializeAsync(string? schemaDdl = null)
    {
        if (IsInitialized) return;

        var escape = Environment.GetEnvironmentVariable("SQL_CONNECTION_STRING");
        var masterEscape = Environment.GetEnvironmentVariable("SQL_SERVER_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(escape) && string.IsNullOrWhiteSpace(masterEscape))
        {
            _container = new ContainerBuilder()
                .WithImage(SqlImage)
                .WithPortBinding(1433, true)
                .WithEnvironment("ACCEPT_EULA", "Y")
                .WithEnvironment("MSSQL_SA_PASSWORD", Password)
                .WithWaitStrategy(Wait.ForUnixContainer().UntilPortIsAvailable(1433))
                .Build();

            await _container.StartAsync();
            Host = "127.0.0.1";
            Port = _container.GetMappedPublicPort(1433);
        }

        await WaitForSqlReadyAsync(TimeSpan.FromSeconds(60));
        await EnsureDatabaseCreatedAsync();
        if (!string.IsNullOrWhiteSpace(schemaDdl))
        {
            await ExecuteNonQueryAsync(schemaDdl);
        }

        IsInitialized = true;
    }

    public async Task ExecuteNonQueryAsync(string sql, Action<SqlParameterCollection>? configureParameters = null)
    {
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(sql, conn);
        configureParameters?.Invoke(cmd.Parameters);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<object?> ExecuteScalarAsync(string sql, Action<SqlParameterCollection>? configureParameters = null)
    {
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(sql, conn);
        configureParameters?.Invoke(cmd.Parameters);
        return await cmd.ExecuteScalarAsync();
    }

    private async Task WaitForSqlReadyAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        Exception? last = null;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                await using var conn = new SqlConnection(MasterConnectionString);
                await conn.OpenAsync();
                await using var cmd = new SqlCommand("SELECT 1;", conn);
                await cmd.ExecuteScalarAsync();
                return;
            }
            catch (Exception ex)
            {
                last = ex;
                await Task.Delay(1000);
            }
        }

        throw new TimeoutException($"SQL Server container did not accept connections within {timeout}. Last error: {last?.Message}", last);
    }

    private async Task EnsureDatabaseCreatedAsync()
    {
        var safeIdentifier = DatabaseName.Replace("]", "]]");
        var safeLiteral = DatabaseName.Replace("'", "''");
        var ddl = $"IF DB_ID(N'{safeLiteral}') IS NULL CREATE DATABASE [{safeIdentifier}];";
        await using var conn = new SqlConnection(MasterConnectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(ddl, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }
}
