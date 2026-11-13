module BotInfra.Tests.ConnectionPoolTests

open BotInfra
open Npgsql
open Xunit

[<Theory>]
[<InlineData(false, 1, 20, 1, 20)>]
[<InlineData(true, 1, 20, 1, 4)>]
[<InlineData(true, 6, 10, 4, 4)>]
[<InlineData(true, 1, 3, 1, 3)>]
let ``Bot connections enable pooling and respect the configured budget`` (capped: bool) (minimum: int) (maximum: int) (expectedMin: int) (expectedMax: int) =
    let input = $"Host=db;Database=example;Username=service;Pooling=false;Minimum Pool Size={minimum};Maximum Pool Size={maximum};Command Timeout=12"
    let budget = if capped then Some 4 else None
    let pooled = DbSettings.pooledConnectionString budget input
    let settings = NpgsqlConnectionStringBuilder(pooled)
    Assert.True(settings.Pooling)
    Assert.Equal<int>(expectedMin, settings.MinPoolSize)
    Assert.Equal<int>(expectedMax, settings.MaxPoolSize)
    Assert.Equal("example", settings.Database)
    Assert.Equal("service", settings.Username)
    Assert.Equal(12, settings.CommandTimeout)
    Assert.Equal(pooled, DbSettings.pooledConnectionString budget pooled)
