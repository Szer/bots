module BotInfra.Tests.PostgresFixture

open System.Threading.Tasks
open Dapper
open Npgsql
open Testcontainers.PostgreSql
open Xunit

/// Event + snapshot tables exactly as EVENTSTORE.md prescribes, plus two sabotaged snapshot tables:
/// `broken_snapshot` rejects every write, `undeletable_snapshot` rejects every delete.
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

-- Snapshots are only valid for an append-only log: any in-place rewrite of events drops them.
CREATE OR REPLACE FUNCTION event_snapshot_invalidate() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'TRUNCATE' THEN
        TRUNCATE event_snapshot;
    ELSE
        DELETE FROM event_snapshot WHERE stream_id = OLD.stream_id OR stream_id = NEW.stream_id;
    END IF;
    RETURN NULL;
END $$;
CREATE OR REPLACE TRIGGER event_snapshot_invalidate AFTER UPDATE OR DELETE ON event
    FOR EACH ROW EXECUTE FUNCTION event_snapshot_invalidate();
CREATE OR REPLACE TRIGGER event_snapshot_invalidate_truncate AFTER TRUNCATE ON event
    FOR EACH STATEMENT EXECUTE FUNCTION event_snapshot_invalidate();

CREATE TABLE broken_snapshot (LIKE event_snapshot INCLUDING ALL);
ALTER TABLE broken_snapshot ADD CONSTRAINT always_fails CHECK (false);

CREATE TABLE undeletable_snapshot (LIKE event_snapshot INCLUDING ALL);
CREATE FUNCTION refuse_delete() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN RAISE EXCEPTION 'deletes refused'; END $$;
CREATE TRIGGER refuse_delete BEFORE DELETE ON undeletable_snapshot FOR EACH ROW EXECUTE FUNCTION refuse_delete();
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
