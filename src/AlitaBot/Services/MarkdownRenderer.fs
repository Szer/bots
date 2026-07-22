/// Minimal Markdig AST -> Telegram MarkdownV2 renderer (Slice 6). We do NOT vendor a
/// third-party MDV2 emitter — this walks Markdig's own parsed AST (CommonMark) and emits
/// Telegram's MarkdownV2 wire syntax directly, escaping every reserved character in plain
/// text runs. Supported elements: bold, italic, inline code, pre/fenced code, links,
/// (un)ordered lists, blockquote, and a lightweight best-effort `||spoiler||` pass over
/// literal text (Markdig has no native spoiler syntax — CommonMark doesn't define one —
/// so it's recognized post-hoc in leaf text nodes, not via the block/inline AST).
/// Everything else Markdig doesn't specifically parse into one of the above (headings,
/// tables, raw HTML, etc.) degrades to escaped plain text — never raw/unescaped output.
module AlitaBot.Services.MarkdownRenderer

open System.Text
open Markdig
open Markdig.Syntax
open Markdig.Syntax.Inlines
open BotInfra

/// Characters MDV2 requires escaped (with a preceding '\') in ordinary text runs — the
/// exact list from Telegram's Bot API docs, plus a literal backslash itself (undefined
/// behavior otherwise: an un-escaped '\' immediately before a later-escaped character
/// would be misread as introducing that escape).
let private reservedChars =
    set [ '\\'; '_'; '*'; '['; ']'; '('; ')'; '~'; '`'; '>'; '#'; '+'; '-'; '='; '|'; '{'; '}'; '.'; '!' ]

/// Escapes a plain-text run for use outside any code/pre/link-URL entity.
let escapeText (s: string) : string =
    let sb = StringBuilder(s.Length)
    for c in s do
        if reservedChars.Contains c then
            %sb.Append('\\').Append(c)
        else
            %sb.Append(c)
    sb.ToString()

/// Escapes the content of a `code`/```pre``` entity — only backslash and backtick.
let private escapeCode (s: string) =
    s.Replace("\\", "\\\\").Replace("`", "\\`")

/// Escapes a link URL — only backslash and the closing paren.
let private escapeLinkUrl (s: string) =
    s.Replace("\\", "\\\\").Replace(")", "\\)")

/// Best-effort `||spoiler||` recognition inside a literal text run: Telegram's MDV2
/// spoiler syntax, which Markdig itself has no concept of. Splits on non-greedy
/// `||...||` pairs, escaping spoiler content and everything outside it independently.
/// An unpaired `||` degrades to plain (escaped) text — never a dangling entity marker.
let private escapeTextWithSpoilers (s: string) : string =
    let sb = StringBuilder(s.Length)
    let mutable i = 0
    while i < s.Length do
        let openIdx = s.IndexOf("||", i, System.StringComparison.Ordinal)
        if openIdx < 0 then
            %sb.Append(escapeText (s.Substring(i)))
            i <- s.Length
        else
            let closeIdx = s.IndexOf("||", openIdx + 2, System.StringComparison.Ordinal)
            if closeIdx < 0 then
                %sb.Append(escapeText (s.Substring(i)))
                i <- s.Length
            else
                %sb.Append(escapeText (s.Substring(i, openIdx - i)))
                let inner = s.Substring(openIdx + 2, closeIdx - openIdx - 2)
                %sb.Append("||").Append(escapeText inner).Append("||")
                i <- closeIdx + 2
    sb.ToString()

/// Renders one inline node (and its children) into `sb`.
let rec private renderInline (sb: StringBuilder) (node: Inline) : unit =
    match node with
    | :? LiteralInline as lit -> %sb.Append(escapeTextWithSpoilers (lit.Content.ToString()))
    | :? CodeInline as code -> %sb.Append('`').Append(escapeCode code.Content).Append('`')
    | :? LineBreakInline -> %sb.Append('\n')
    | :? EmphasisInline as em ->
        // CommonMark strong (** or __, DelimiterCount >= 2) -> MDV2 bold (*...*);
        // CommonMark emphasis (single * or _) -> MDV2 italic (_..._).
        let marker = if em.DelimiterCount >= 2 then "*" else "_"
        %sb.Append(marker)
        renderContainerInline sb em
        %sb.Append(marker)
    | :? LinkInline as link ->
        if link.IsImage then
            // No inline image entity in MDV2 — render the alt text, escaped, same as any
            // other unsupported construct.
            renderContainerInline sb link
        else
            let url = if isNull link.Url then "" else link.Url
            %sb.Append('[')
            renderContainerInline sb link
            %sb.Append("](").Append(escapeLinkUrl url).Append(')')
    | :? ContainerInline as container -> renderContainerInline sb container
    | _ ->
        // Unknown inline (autolink, HTML inline, etc.) — fall back to its own literal
        // span of the source text, escaped.
        %sb.Append(escapeText (node.ToString()))

and private renderContainerInline (sb: StringBuilder) (container: ContainerInline) : unit =
    let mutable child = container.FirstChild
    while not (isNull child) do
        renderInline sb child
        child <- child.NextSibling

let private renderInlines (sb: StringBuilder) (inlineContainer: ContainerInline) =
    if not (isNull inlineContainer) then
        renderContainerInline sb inlineContainer

/// Prefixes every non-empty line of `text` with "> " (MDV2's blockquote marker — literal,
/// not an escaped character) and returns the joined result, no trailing newline.
let private quotePrefix (text: string) : string =
    text.TrimEnd('\n').Split('\n')
    |> Array.map (fun line -> "> " + line)
    |> String.concat "\n"

/// Raw source text spanned by `block` (`Block.Span` is a pair of character offsets into
/// the original document) — Markdig 1.x's `CodeBlock`/`FencedCodeBlock` no longer expose
/// a convenient `.Lines` accumulator, so code content is recovered straight from the
/// source instead of an AST field.
let private sourceSpanOf (source: string) (block: Block) : string =
    let span = block.Span
    if span.Start < 0 || span.End < span.Start || span.End >= source.Length then
        ""
    else
        source.Substring(span.Start, span.End - span.Start + 1)

/// Fenced code (```lang\n...\n```) body: the raw span minus its opening fence line
/// (``` + optional info string) and, when present, its closing fence line (absent for an
/// unterminated fence running to EOF — CommonMark still parses that as best-effort code).
let private fencedCodeBody (source: string) (fc: FencedCodeBlock) : string =
    let raw = sourceSpanOf source fc
    let lines = raw.Replace("\r\n", "\n").Split('\n')
    if lines.Length <= 1 then
        ""
    else
        let withoutOpen = lines[1..]
        let withoutClose =
            if withoutOpen.Length > 0 && withoutOpen[withoutOpen.Length - 1].TrimStart().StartsWith("```") then
                withoutOpen[.. withoutOpen.Length - 2]
            else
                withoutOpen
        String.concat "\n" withoutClose

/// Indented (4-space) code block body: the raw span with up to 4 leading spaces of
/// indentation stripped from each line (CommonMark's own definition of the block's
/// content — the indentation itself is not part of the code).
let private indentedCodeBody (source: string) (cb: CodeBlock) : string =
    let raw = sourceSpanOf source cb
    raw.Replace("\r\n", "\n").Split('\n')
    |> Array.map (fun line -> if line.StartsWith("    ") then line.Substring(4) else line.TrimStart(' '))
    |> String.concat "\n"

/// Renders one block (and any nested blocks) into `sb`. `sb` always ends up with a
/// trailing blank line after a block-level element; callers trim the final result.
/// `source` is the original markdown text (code blocks recover their content from it).
let rec private renderBlock (source: string) (sb: StringBuilder) (block: Block) : unit =
    match block with
    | :? ParagraphBlock as p ->
        renderInlines sb p.Inline
        %sb.Append("\n\n")
    | :? HeadingBlock as h ->
        // MDV2 has no heading entity — bold the text instead (matches how Telegram
        // clients themselves render CommonMark headings pasted as plain messages).
        %sb.Append('*')
        renderInlines sb h.Inline
        %sb.Append("*\n\n")
    | :? QuoteBlock as q ->
        let inner = StringBuilder()
        for sub in q do
            renderBlock source inner sub
        %sb.Append(quotePrefix (inner.ToString())).Append("\n\n")
    | :? FencedCodeBlock as fc ->
        let code = fencedCodeBody source fc
        let lang = if isNull fc.Info then "" else fc.Info
        %sb.Append("```").Append(lang).Append('\n').Append(escapeCode code).Append("\n```\n\n")
    | :? CodeBlock as cb ->
        let code = indentedCodeBody source cb
        %sb.Append("```\n").Append(escapeCode code).Append("\n```\n\n")
    | :? ListBlock as list ->
        let mutable idx = if list.IsOrdered then (match System.Int32.TryParse(list.OrderedStart) with true, v -> v | _ -> 1) else 1
        for item in list do
            match item with
            | :? ListItemBlock as li ->
                let marker = if list.IsOrdered then escapeText (string idx) + "\\." else "•"
                %sb.Append(marker).Append(' ')
                let inner = StringBuilder()
                for sub in li do
                    renderBlock source inner sub
                %sb.Append(inner.ToString().Trim('\n')).Append('\n')
                idx <- idx + 1
            | other -> renderBlock source sb other
        %sb.Append('\n')
    | :? ContainerBlock as container ->
        for sub in container do
            renderBlock source sb sub
    | _ -> ()

/// Parses `markdown` (CommonMark, via Markdig's default pipeline) and renders it into
/// Telegram MarkdownV2 wire syntax. Pure/side-effect-free — callers decide what to do
/// with the result (send as-is, fall back to plain text on a Telegram 400, etc.).
let toMarkdownV2 (markdown: string) : string =
    let doc = Markdown.Parse(markdown)
    let sb = StringBuilder(markdown.Length)
    for block in doc do
        renderBlock markdown sb block
    sb.ToString().Trim('\n')
