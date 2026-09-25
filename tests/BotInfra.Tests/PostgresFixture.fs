module BotInfra.Tests.PostgresFixture

open System.Threading.Tasks
open Dapper
open Npgsql
open Testcontainers.PostgreSql
open Xunit

/// Event + snapshot tables exactly as EVENTSTORE.md prescribes. `broken_snapshot` rejects every
/// write, to prove a failing snapshot write never fails a load or an append.
let private schemaSql =
    """
CREATE TABLE event (
    id              BIGSERIAL   PRIMARY KEY,
    stream_id       TEXT        NOT NULL,
    stream_version  INT         NOT NULL,
    event_type      TEXT        GENERATED ALWAYS AS (data->>'Case') STORED,
    data            JSONB       NOT NULL,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    UNIQUE (stream_id, stream_version)
);

CREATE TABLE event_snapshot (
    stream_id       TEXT        NOT NULL,
    state_type      TEXT        NOT NULL,
    schema_version  INT         NOT NULL,
    stream_version  INT         NOT NULL,
    state           JSONB       NOT NULL,
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (stream_id, state_type)
);

CREATE TABLE broken_snapshot (LIKE event_snapshot INCLUDING ALL);
ALTER TABLE broken_snapshot ADD CONSTRAINT always_fails CHECK (false);
"""

type PostgresFixture() =
    let container = PostgreSqlBuilder("postgres:17.10").Build()

    member _.ConnectionString = container.GetConnectionString()

    interface IAsyncLifetime with
        member this.InitializeAsync() =
            ValueTask(task {
                do! container.StartAsync()
                use conn = new NpgsqlConnection(this.ConnectionString)
                let! _ = conn.ExecuteAsync schemaSql
                return ()
            })

    interface System.IAsyncDisposable with
        member _.DisposeAsync() = container.DisposeAsync()
