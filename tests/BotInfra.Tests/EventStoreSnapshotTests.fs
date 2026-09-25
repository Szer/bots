module BotInfra.Tests.EventStoreSnapshotTests

open System
open System.Collections.Concurrent
open System.Diagnostics
open System.Text.Json
open System.Text.Json.Serialization
open System.Threading.Tasks
open Dapper
open Npgsql
open BotInfra
open BotInfra.Tests.PostgresFixture
open Xunit

type TallyEvent =
    | Added of {| amount: int |}
    | Renamed of {| name: string option |}

type Tally =
    { Total: int
      Name: string option
      Count: int }
    static member Zero = { Total = 0; Name = None; Count = 0 }
    static member Fold(s: Tally, e: TallyEvent) =
        match e with
        | Added a -> { s with Total = s.Total + a.amount; Count = s.Count + 1 }
        | Renamed r -> { s with Name = r.name; Count = s.Count + 1 }

let private fold s e = Tally.Fold(s, e)

let private jsonOpts =
    JsonFSharpOptions.Default()
        .WithUnionInternalTag()
        .WithUnionUnwrapRecordCases()
        .WithUnionNamedFields()
        .WithUnwrapOption()
        .WithSkippableOptionFields(SkippableOptionFields.Always, deserializeNullAsNone = true)
        .ToJsonSerializerOptions()

let private policy every = { StateType = "Tally"; SchemaVersion = 1; SnapshotEvery = every }

/// Every `eventStore.load` span stopped while the capture is alive.
type private LoadSpans() =
    let spans = ConcurrentQueue<Activity>()
    let listener =
        new ActivityListener(
            ShouldListenTo = (fun s -> s.Name = "BotInfra.EventStore"),
            Sample = (fun _ -> ActivitySamplingResult.AllDataAndRecorded),
            ActivityStopped = (fun a -> if a.OperationName = "eventStore.load" then spans.Enqueue a))
    do ActivitySource.AddActivityListener listener
    member _.All = List.ofSeq spans
    member _.Last = Seq.last spans
    interface IDisposable with
        member _.Dispose() = listener.Dispose()

let private tag (key: string) (a: Activity) = a.GetTagItem key |> string
let private hasEvent (name: string) (a: Activity) = a.Events |> Seq.exists (fun e -> e.Name = name)

type SnapshotRow = { schema_version: int; stream_version: int; state: string }

type EventStoreSnapshotTests(db: PostgresFixture) =
    let store = EventStore(db.ConnectionString, "event", jsonOpts, "event_snapshot")
    let newStream () = $"tally:{Guid.NewGuid():N}"

    let appendRaw (streamId: string) (events: TallyEvent list) = task {
        let! (_, version) = store.GetRawEventsForStream streamId
        match! store.TryAppend(streamId, version, events) with
        | Ok () -> ()
        | Error _ -> failwith "unexpected concurrency conflict"
    }

    let added n = [ for i in 1 .. n -> Added {| amount = i |} ]

    let replay streamId = store.FoldEvents(fold, Tally.Zero, streamId)

    let snapshotRow (table: string) (streamId: string) = task {
        use conn = new NpgsqlConnection(db.ConnectionString)
        let! rows =
            conn.QueryAsync<SnapshotRow>(
                $"SELECT schema_version, stream_version, state::TEXT AS state FROM {table} WHERE stream_id = @streamId",
                {| streamId = streamId |})
        return Seq.tryHead rows
    }

    let exec (sql: string) (streamId: string) = task {
        use conn = new NpgsqlConnection(db.ConnectionString)
        let! _ = conn.ExecuteAsync(sql, {| streamId = streamId |})
        return ()
    }

    let load p streamId = store.LoadState(fold, Tally.Zero, p, streamId)

    let increment p streamId amount =
        store.Transact(fold, Tally.Zero, p, (fun _ -> [ Added {| amount = amount |} ]), streamId)

    /// The core invariant: a stored snapshot always equals the fold of the log up to its version.
    let assertSnapshotMatchesLog (streamId: string) = task {
        match! snapshotRow "event_snapshot" streamId with
        | None -> ()
        | Some row ->
            let! (raws, _) = EventStore(db.ConnectionString, "event", jsonOpts).GetRawEventsForStream streamId
            let expected =
                raws
                |> List.filter (fun r -> r.stream_version <= row.stream_version)
                |> List.map (fun r -> JsonSerializer.Deserialize<TallyEvent>(r.data, jsonOpts))
                |> List.fold fold Tally.Zero
            Assert.Equal(expected, JsonSerializer.Deserialize<Tally>(row.state, jsonOpts))
    }

    [<Fact>]
    member _.``first load replays the stream and snapshots it once the tail reaches SnapshotEvery``() = task {
        let sid = newStream ()
        do! appendRaw sid (added 5)
        let! (state, version) = load (policy 3) sid
        let! expected = replay sid
        Assert.Equal(expected, state)
        Assert.Equal(5, version)
        let! row = snapshotRow "event_snapshot" sid
        Assert.Equal(Some 5, row |> Option.map _.stream_version)

        use spans = new LoadSpans()
        let! (again, _) = load (policy 3) sid
        Assert.Equal(expected, again)
        Assert.Equal("0", tag "event_count" spans.Last)
        Assert.True(hasEvent "snapshot.hit" spans.Last)
    }

    [<Fact>]
    member _.``no snapshot is written while the tail is below SnapshotEvery``() = task {
        let sid = newStream ()
        do! appendRaw sid (added 2)
        let! (state, version) = load (policy 3) sid
        Assert.Equal(3, state.Total)
        Assert.Equal(2, version)
        let! row = snapshotRow "event_snapshot" sid
        Assert.Equal(None, row)
    }

    [<Fact>]
    member _.``missing stream loads as zero at version 0``() = task {
        let! (state, version) = load (policy 1) (newStream ())
        Assert.Equal(Tally.Zero, state)
        Assert.Equal(0, version)
    }

    [<Fact>]
    member _.``a stored snapshot is used instead of replaying the prefix``() = task {
        let sid = newStream ()
        do! appendRaw sid (added 3)
        let! _ = load (policy 1) sid
        do! exec """UPDATE event_snapshot SET state = jsonb_set(state, '{Total}', '1000') WHERE stream_id = @streamId""" sid
        do! appendRaw sid [ Added {| amount = 1 |} ]
        let! (state, version) = load (policy 100) sid
        Assert.Equal(1001, state.Total)
        Assert.Equal(4, version)
    }

    [<Fact>]
    member _.``snapshot plus tail equals full replay across snapshot boundaries``() = task {
        let sid = newStream ()
        let p = policy 3
        for i in 1 .. 10 do
            let! _ =
                store.Transact(fold, Tally.Zero, p,
                    (fun (s: Tally) ->
                        if i % 4 = 0 then [ Renamed {| name = Some $"n{s.Total}" |} ]
                        else [ Added {| amount = i |} ]),
                    sid)
            let! (loaded, version) = load p sid
            let! expected = replay sid
            Assert.Equal(expected, loaded)
            Assert.Equal(i, version)
        let! row = snapshotRow "event_snapshot" sid
        Assert.Equal(Some 9, row |> Option.map _.stream_version)
    }

    [<Fact>]
    member _.``a snapshot from another schema version is ignored and overwritten``() = task {
        let sid = newStream ()
        do! appendRaw sid (added 4)
        let! _ = load (policy 1) sid
        do! exec """UPDATE event_snapshot SET state = jsonb_set(state, '{Total}', '1000') WHERE stream_id = @streamId""" sid

        use spans = new LoadSpans()
        let v2 = { policy 1 with SchemaVersion = 2 }
        let! (state, _) = load v2 sid
        Assert.Equal(10, state.Total)
        Assert.True(hasEvent "snapshot.miss" spans.Last)
        Assert.Equal("4", tag "event_count" spans.Last)
        let! row = snapshotRow "event_snapshot" sid
        Assert.Equal(Some 2, row |> Option.map _.schema_version)
        Assert.DoesNotContain("1000", row.Value.state)
    }

    [<Fact>]
    member _.``a snapshot ahead of its log is discarded and rebuilt``() = task {
        let sid = newStream ()
        do! appendRaw sid (added 4)
        let! _ = load (policy 1) sid
        do! exec "UPDATE event_snapshot SET stream_version = 999 WHERE stream_id = @streamId" sid

        use spans = new LoadSpans()
        let! (state, version) = load (policy 1) sid
        Assert.Equal(10, state.Total)
        Assert.Equal(4, version)
        Assert.True(hasEvent "snapshot.discarded_ahead" spans.Last)
        let! row = snapshotRow "event_snapshot" sid
        Assert.Equal(Some 4, row |> Option.map _.stream_version)
    }

    [<Fact>]
    member _.``an unreadable snapshot is discarded and rebuilt``() = task {
        let sid = newStream ()
        do! appendRaw sid (added 4)
        let! _ = load (policy 1) sid
        do! exec """UPDATE event_snapshot SET state = '{"unexpected": true}' WHERE stream_id = @streamId""" sid

        use spans = new LoadSpans()
        let! (state, _) = load (policy 1) sid
        Assert.Equal(10, state.Total)
        Assert.True(hasEvent "snapshot.discarded_unreadable" spans.Last)
        let! row = snapshotRow "event_snapshot" sid
        Assert.Contains("\"Total\": 10", row.Value.state)
    }

    [<Fact>]
    member _.``state types on the same stream keep separate snapshots``() = task {
        let sid = newStream ()
        do! appendRaw sid (added 2)
        let! _ = load (policy 1) sid
        let! _ = load { policy 1 with StateType = "TallyShadow" } sid
        use conn = new NpgsqlConnection(db.ConnectionString)
        let! n = conn.ExecuteScalarAsync<int64>("SELECT count(*) FROM event_snapshot WHERE stream_id = @streamId", {| streamId = sid |})
        Assert.Equal(2L, n)
    }

    [<Fact>]
    member _.``a failing snapshot write never fails the load or the append``() = task {
        let broken = EventStore(db.ConnectionString, "event", jsonOpts, "broken_snapshot")
        let sid = newStream ()
        do! appendRaw sid (added 3)
        use spans = new LoadSpans()
        let! (state, version) = broken.LoadState(fold, Tally.Zero, policy 1, sid)
        Assert.Equal(6, state.Total)
        Assert.Equal(3, version)
        Assert.True(hasEvent "snapshot.write_failed" spans.Last)
        Assert.Equal(ActivityStatusCode.Error, spans.Last.Status)

        let! (_, after) = broken.Transact(fold, Tally.Zero, policy 1, (fun _ -> [ Added {| amount = 4 |} ]), sid)
        Assert.Equal(10, after.Total)
        let! persisted = replay sid
        Assert.Equal(10, persisted.Total)
    }

    [<Fact>]
    member _.``concurrent snapshot transacts on one stream lose no events``() = task {
        let sid = newStream ()
        let p = policy 2
        let! _ = Task.WhenAll [ for _ in 1 .. 12 -> increment p sid 1 :> Task ]
        let! (state, version) = load p sid
        Assert.Equal(12, state.Total)
        Assert.Equal(12, version)
        let! expected = replay sid
        Assert.Equal(expected, state)
    }

    [<Fact>]
    member _.``request scope serves repeated loads from memory and reflects its own appends``() = task {
        let sid = newStream ()
        do! appendRaw sid (added 2)
        use _scope = store.BeginRequestScope()
        use spans = new LoadSpans()
        let! _ = load (policy 5) sid
        let! _ = load (policy 5) sid
        Assert.Equal<string list>([ "db"; "cache" ], spans.All |> List.map (tag "source"))

        let! _ = increment (policy 5) sid 10
        let! (state, version) = load (policy 5) sid
        Assert.Equal(13, state.Total)
        Assert.Equal(3, version)
        Assert.Equal("cache", tag "source" spans.Last)
    }

    [<Fact>]
    member _.``an append through the raw API invalidates the scoped snapshot state``() = task {
        let sid = newStream ()
        do! appendRaw sid (added 2)
        use _scope = store.BeginRequestScope()
        let! _ = load (policy 5) sid
        let! _ = store.Transact(fold, Tally.Zero, (fun _ -> [ Added {| amount = 10 |} ]), sid)
        use spans = new LoadSpans()
        let! (state, version) = load (policy 5) sid
        Assert.Equal(13, state.Total)
        Assert.Equal(3, version)
        Assert.Equal("db", tag "source" spans.Last)
    }

    [<Fact>]
    member _.``a snapshot append invalidates the scoped raw stream``() = task {
        let sid = newStream ()
        do! appendRaw sid (added 2)
        use _scope = store.BeginRequestScope()
        let! _ = store.GetRawEventsForStream sid
        let! _ = increment (policy 5) sid 10
        let! (raws, version) = store.GetRawEventsForStream sid
        Assert.Equal(3, version)
        Assert.Equal(3, raws.Length)
    }

    [<Fact>]
    member _.``concurrent loads and appends never store a snapshot that disagrees with the log``() = task {
        let sid = newStream ()
        for round in 1 .. 5 do
            let work =
                [ for i in 1 .. 16 ->
                    let p = policy (1 + i % 3)
                    if i % 4 = 0 then load p sid :> Task
                    else increment p sid 1 :> Task ]
            do! Task.WhenAll work
            do! assertSnapshotMatchesLog sid
            let! (state, version) = load (policy 1) sid
            let! expected = replay sid
            Assert.Equal(expected, state)
            Assert.Equal(round * 12, version)
            Assert.Equal(round * 12, state.Total)
    }

    [<Fact>]
    member _.``raw and snapshot writers racing on one stream stay consistent``() = task {
        let sid = newStream ()
        let work =
            [ for i in 1 .. 30 ->
                if i % 2 = 0 then increment (policy 2) sid 1 :> Task
                else store.Transact(fold, Tally.Zero, (fun _ -> [ Added {| amount = 1 |} ]), sid) :> Task ]
        do! Task.WhenAll work
        do! assertSnapshotMatchesLog sid
        let! (state, version) = load (policy 2) sid
        Assert.Equal(30, state.Total)
        Assert.Equal(30, version)
    }

    [<Fact>]
    member _.``a stale scoped state conflicts, re-reads the log and appends on top``() = task {
        let sid = newStream ()
        do! appendRaw sid (added 2)
        let otherPod = EventStore(db.ConnectionString, "event", jsonOpts, "event_snapshot")
        use _scope = store.BeginRequestScope()
        let! _ = load (policy 1) sid
        let! _ = otherPod.Transact(fold, Tally.Zero, policy 1, (fun _ -> [ Added {| amount = 100 |} ]), sid)

        let mutable decisions = 0
        let! (_, state) =
            store.Transact(fold, Tally.Zero, policy 1, (fun _ -> decisions <- decisions + 1; [ Added {| amount = 10 |} ]), sid)
        Assert.Equal(2, decisions)
        Assert.Equal(113, state.Total)
        let! (reloaded, version) = load (policy 1) sid
        Assert.Equal(113, reloaded.Total)
        Assert.Equal(4, version)
        do! assertSnapshotMatchesLog sid
    }

    [<Fact>]
    member _.``parallel appends to many streams inside one request scope are all cached correctly``() = task {
        let sids = [ for _ in 1 .. 200 -> newStream () ]
        use _scope = store.BeginRequestScope()
        let! _ =
            Task.WhenAll [
                for sid in sids do
                    yield task { let! _ = increment (policy 1) sid 5 in () }
                    yield task { let! _ = store.Transact(fold, Tally.Zero, (fun _ -> [ Added {| amount = 1 |} ]), $"{sid}:raw") in () } ]
        for sid in sids do
            let! (state, _) = load (policy 1) sid
            Assert.Equal(5, state.Total)
            let! raw = store.FoldEvents(fold, Tally.Zero, $"{sid}:raw")
            Assert.Equal(1, raw.Total)
    }

    [<Fact>]
    member _.``snapshot policy requires a snapshot table``() =
        let plain = EventStore(db.ConnectionString, "event", jsonOpts)
        Assert.ThrowsAsync<InvalidOperationException>(fun () ->
            plain.LoadState(fold, Tally.Zero, policy 1, newStream ()) :> Task)

    [<Fact>]
    member _.``snapshot policy rejects a non-positive SnapshotEvery``() =
        Assert.ThrowsAsync<ArgumentException>(fun () -> load (policy 0) (newStream ()) :> Task)
