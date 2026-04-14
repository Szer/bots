namespace BotInfra

open System
open System.Threading.Tasks
open Dapper
open Npgsql

/// Shared distributed scheduled-job locking.
/// Uses an UPDATE…RETURNING pattern so the acquire is atomic with no advisory locks.
module ScheduledJobs =

    /// Tries to acquire a scheduled job lease for the given pod.
    /// Returns true if this pod successfully locked the job
    /// (meaning the job is due and no other pod currently holds the lock).
    let tryAcquire
            (connString: string)
            (timeProvider: TimeProvider)
            (jobName: string)
            (scheduledTime: TimeSpan)
            (podId: string) : Task<bool> = task {
        use conn = new NpgsqlConnection(connString)
        let now = timeProvider.GetUtcNow().UtcDateTime
        //language=postgresql
        let sql =
            """
UPDATE scheduled_job
SET locked_until = @now + INTERVAL '1 hour',
    locked_by    = @podId
WHERE job_name = @jobName
  AND @now >= (CURRENT_DATE + @scheduledTime)
  AND (last_completed_at IS NULL OR last_completed_at < (CURRENT_DATE + @scheduledTime))
  AND (locked_until IS NULL OR locked_until < @now)
RETURNING job_name;
            """
        let! result =
            conn.QueryAsync<string>(sql,
                {| jobName       = jobName
                   scheduledTime = scheduledTime
                   podId         = podId
                   now           = now |})
        return Seq.length result > 0
    }

    /// Releases the lease and records completion time for a scheduled job.
    let complete
            (connString: string)
            (timeProvider: TimeProvider)
            (jobName: string) : Task = task {
        use conn = new NpgsqlConnection(connString)
        let now = timeProvider.GetUtcNow().UtcDateTime
        //language=postgresql
        let sql =
            """
UPDATE scheduled_job
SET last_completed_at = @now,
    locked_until      = NULL,
    locked_by         = NULL
WHERE job_name = @jobName;
            """
        let! _ = conn.ExecuteAsync(sql, {| jobName = jobName; now = now |})
        return ()
    }
