namespace AlitaBot.Services

open System
open Dapper
open Npgsql

[<CLIMutable>]
type MessageLogRow =
    { chat_id: int64
      message_id: int64
      user_id: int64
      username: string | null
      display_name: string
      is_bot: bool
      reply_to_message_id: Nullable<int64>
      text: string
      sent_at: DateTime }

type DbService(connString: string) =
    let openConn() = task {
        let conn = new NpgsqlConnection(connString)
        do! conn.OpenAsync()
        return conn
    }

    /// Idempotent insert: webhook redelivery of the same (chat_id, message_id) is a no-op.
    member _.LogMessage(row: MessageLogRow) =
        task {
            use! conn = openConn()
            //language=postgresql
            let sql =
                """
INSERT INTO message_log (chat_id, message_id, user_id, username, display_name, is_bot, reply_to_message_id, text, sent_at)
VALUES (@chat_id, @message_id, @user_id, @username, @display_name, @is_bot, @reply_to_message_id, @text, @sent_at)
ON CONFLICT (chat_id, message_id) DO NOTHING;
"""
            let! _ = conn.ExecuteAsync(sql, row)
            return ()
        }

    /// Last `n` messages of the chat, returned in chronological order.
    member _.RecentContext(chatId: int64, n: int) =
        task {
            use! conn = openConn()
            //language=postgresql
            let sql =
                """
SELECT chat_id, message_id, user_id, username, display_name, is_bot, reply_to_message_id, text, sent_at
FROM (SELECT * FROM message_log WHERE chat_id = @chat_id ORDER BY sent_at DESC, id DESC LIMIT @n) recent
ORDER BY sent_at ASC, id ASC;
"""
            let! rows = conn.QueryAsync<MessageLogRow>(sql, {| chat_id = chatId; n = n |})
            return rows |> Seq.toArray
        }
