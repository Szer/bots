namespace AlitaBot.Services

open System.Text.Json
open AlitaBot

/// Admin-user resolution — extracted from BotService (S10 PR1 prerequisite) so the NL
/// tool-calling loop (ToolRegistry.availableToolDefs, ResponderService) can gate
/// AdminOnly tools the same way `/sql` gates itself, without depending on BotService.
module Admin =
    /// Parses the ADMIN_USER_IDS bot_setting (JSON_BLOB array of ints) — lenient: malformed
    /// JSON or a non-array value -> [] (nobody is admin until the setting is fixed, never
    /// "everybody").
    let parseAdminUserIds (json: string) : int64 list =
        try
            use doc = JsonDocument.Parse(json: string)
            if doc.RootElement.ValueKind <> JsonValueKind.Array then
                []
            else
                [ for el in doc.RootElement.EnumerateArray() do
                    if el.ValueKind = JsonValueKind.Number then el.GetInt64() ]
        with _ -> []

    let isAdmin (conf: BotConfiguration) (userId: int64) : bool =
        parseAdminUserIds conf.AdminUserIdsJson |> List.contains userId
