/// Detects AlitaBot's transient-vs-generic RU fallback text on a `/img`/`/song` failure
/// (see BotService.fs's `handleImageCommand`/`handleSongCommand`, item 4 of the staging-
/// feedback slice: Gemini `503 UNAVAILABLE` "high demand" gets a distinct RU reply —
/// "Модель перегружена..." — instead of the generic "Не получилось..." shrug). Pure/no
/// I/O on purpose so it's trivially unit-testable and reusable from both
/// ImageGenRealTests and SongRealTests, which both edit a "рисую.../сочиняю..."
/// placeholder message in place on failure (never a new message) — see
/// `TgUserClient.AwaitMediaOrPlaceholderEdit`'s doc comment for why that matters for
/// how these real tests observe the outcome.
module AlitaBot.RealTests.GeminiTransientDetection

[<RequireQualifiedAccess>]
type ReplyOutcome =
    /// The placeholder hasn't been touched yet — generation may still be in progress.
    | StillPending
    /// Edited to the transient-fallback text ("Модель перегружена...") — a Gemini 503
    /// "high demand" (or similar rate-limited) response. Real tests skip on this, per
    /// user directive: "let's skip tests on 503 high demand ... not waste money".
    | Transient
    /// Edited to some other (generic failure) text — a genuine regression worth failing on.
    | GenericFailure

/// `placeholderText` is the ORIGINAL, unedited placeholder text ("рисую..."/"сочиняю...")
/// captured right after it was sent; `currentText` is what the message reads now.
/// Matches on the substring shared by both `/img` and `/song`'s transient RU replies
/// ("Модель перегружена...") rather than either exact string, so minor future wording
/// tweaks on one path don't silently break detection on the other.
let classify (placeholderText: string) (currentText: string) : ReplyOutcome =
    if currentText = placeholderText then
        ReplyOutcome.StillPending
    elif currentText.Contains "Модель перегружена" then
        ReplyOutcome.Transient
    else
        ReplyOutcome.GenericFailure

/// Pure unit coverage for `classify` — no Telegram/fixture needed, unlike every other
/// test in this project.
type GeminiTransientDetectionTests() =
    [<Xunit.Fact>]
    member _.``unedited placeholder is still pending``() =
        Xunit.Assert.Equal(ReplyOutcome.StillPending, classify "рисую..." "рисую...")

    [<Xunit.Fact>]
    member _.``edited to the transient RU reply is classified Transient``() =
        Xunit.Assert.Equal(
            ReplyOutcome.Transient,
            classify "сочиняю..." "Модель перегружена, попробуй сочинить чуть позже 🙏")

    [<Xunit.Fact>]
    member _.``edited to the generic RU shrug is classified GenericFailure``() =
        Xunit.Assert.Equal(ReplyOutcome.GenericFailure, classify "рисую..." "Не получилось нарисовать 🙁")
