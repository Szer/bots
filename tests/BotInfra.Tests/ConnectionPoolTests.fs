module BotInfra.Tests.ConnectionPoolTests

open BotInfra
open Npgsql
open Xunit

[<Theory>]
[<InlineData(3, 1, 20, 1, 3)>]
[<InlineData(2, 1, 20, 1, 2)>]
[<InlineData(3, 6, 10, 3, 3)>]
[<InlineData(5, 1, 3, 1, 3)>]
let ``Bot settings bound the pool within the connection string ceiling`` (budget: int) (minimum: int) (maximum: int) (expectedMin: int) (expectedMax: int) =
    let input = $"Host=db;Database=example;Username=service;Pooling=false;Minimum Pool Size={minimum};Maximum Pool Size={maximum};Command Timeout=12"
    let rows = readOnlyDict ["DB_MAX_POOL_SIZE", string budget]
    let pooled = DbSettings.pooledConnectionString 2 rows input
    let settings = NpgsqlConnectionStringBuilder(pooled)
    Assert.True(settings.Pooling)
    Assert.Equal<int>(expectedMin, settings.MinPoolSize)
    Assert.Equal<int>(expectedMax, settings.MaxPoolSize)
    Assert.Equal("example", settings.Database)
    Assert.Equal("service", settings.Username)
    Assert.Equal(12, settings.CommandTimeout)
    Assert.Equal(pooled, DbSettings.pooledConnectionString 2 rows pooled)

[<Fact>]
let ``Invalid budgets fail startup before the settings listener can starve requests`` () =
    let input = "Host=db;Maximum Pool Size=20"
    Assert.Throws<System.InvalidOperationException>(fun () -> DbSettings.pooledConnectionString 2 (readOnlyDict []) input |> ignore) |> ignore
    for value in ["oops"; "1"] do
        Assert.Throws<System.ArgumentException>(fun () ->
            DbSettings.pooledConnectionString 2 (readOnlyDict ["DB_MAX_POOL_SIZE",value]) input |> ignore) |> ignore
    Assert.Throws<System.InvalidOperationException>(fun () ->
        DbSettings.pooledConnectionString 2 (readOnlyDict ["DB_MAX_POOL_SIZE","2"]) "Host=db;Maximum Pool Size=1" |> ignore) |> ignore
