# Compaction Architecture

`ChatAgentService` keeps a chat session's wire payload to the model bounded so long agentic turns
don't silently overflow the context window. Before this feature, the only mechanism was
`DropOldHistory`: once history exceeded `Agent:HistoryKeepRecentMessages` (default 40), everything
older was cut off the front and replaced with a one-line note — the content was gone for good, even
though the full history was still sitting in the DB. Compaction replaces that drop with a summarize:
old history is folded into a running summary instead of being silently discarded, so a session can
run indefinitely without either overflowing the model's context or losing what happened early on.


Scope: `ChatAgentService` (interactive chat sessions) only. Skills/Agents
(`SkillExecutionService`/`AgentExecutionService`) run short, bounded, non-persisted tool loops — they
don't accumulate long-lived history the way a chat session does, so compaction doesn't apply there.

## Three zones

Every turn, a session's history splits into three zones, oldest to newest:

```
 [ compacted ]                 [ pending ]                    [ kept window ]
 SequenceNumber                SequenceNumber                 last HistoryKeepRecentMessages
 <= SummarizedThroughSeq#      > cursor, older than the        messages — always raw, never a
                                kept window — raw until a       compaction candidate
                                compaction pass folds them in
        |                              |                               |
        v                              v                               v
 one system message           sent to the model            sent to the model
 carrying HistorySummary       raw, as-is                   raw, as-is
```

1. **Compacted** — every `ChatMessage` with `SequenceNumber <= ChatSession.SummarizedThroughSequenceNumber`.
   Represented in the wire payload by a single system message carrying `ChatSession.HistorySummary`
   instead of the raw messages.
2. **Pending** — older than the kept window but not yet folded in, because a compaction pass hasn't
   triggered yet. Sent raw — nothing is lost, compaction just hasn't caught up. This is the zone that
   `DropOldHistory` used to silently drop.
3. **Kept window** — the last `Agent:HistoryKeepRecentMessages` messages. Always raw, never a
   compaction candidate — unchanged from before this feature.

## Persistence

Two columns on `ChatSession` (`src/PersonalAI.Core/Models/Entities/ChatSession.cs`):

- `HistorySummary` (`string?`) — the running compacted summary. `null` means nothing has been
  compacted yet.
- `SummarizedThroughSequenceNumber` (`int`) — cursor: the highest `ChatMessage.SequenceNumber`
  already folded into `HistorySummary`. `0` means nothing has been compacted yet.

Migration: `20260923104228_AddChatSessionHistoryCompaction`. No separate summary table or version
history — a single mutable blob per session is enough; nothing needs to look at what an older summary
used to say.

Full raw history is never deleted from the DB — only the wire payload sent to the model each turn is
affected, same as `DropOldHistory` before it. `repo.GetMessagesAsync` always returns everything.

## Trigger — dual, batched

Summarizing on every single turn once the kept window is exceeded would add a guaranteed extra LLM
round-trip to every subsequent turn forever, so compaction is batched: it accumulates pending messages
and only runs once **either** threshold is crossed, whichever comes first:

- pending message count >= `Agent:CompactionBatchSize` (default `20`), or
- pending zone's total *already-capped* character count >= `Agent:CompactionMaxCharsPerCall` (default `24000`)

Both triggers are needed. Count alone is unsafe for a chatty tool like `sandbox`: each pending
Tool-role message is already capped to `Agent:ToolResultMaxChars` (4000 by default) via the existing
`CapContent`, but a handful of near-cap `sandbox exec` outputs can push the pending zone's wire size up
fast — 6 outputs at ~4000 chars each is already 24,000 chars, well before 20 *messages* accumulate.
Chars alone is unsafe the other way: many small plain-chat messages could sit pending indefinitely
without ever crossing a char threshold. `CompactionMaxCharsPerCall` does double duty as both this
trigger and the per-call fold-in budget below, so no extra setting exists purely for the trigger.

Below both thresholds, pending messages are just sent raw — zone 2 behaves exactly like zone 3 until
compaction catches up.

## Bounding a first-time large backlog

A session that's accumulated hundreds of messages before compaction is enabled (or before it first
crosses the kept window) could otherwise need one huge, possibly-overflowing summarization call.
Folding walks the pending list oldest-to-newest, accumulating each message's (already-capped) length,
and stops before exceeding `Agent:CompactionMaxCharsPerCall` — except the very first message in a pass
is always included regardless of size, so a single oversized message can't stall progress forever. A
large legacy backlog therefore catches up over several turns instead of one call.

## Never splitting a tool call from its result

A Tool-role message can never be sent to the model without the Assistant message that requested it —
same invariant `DropOldHistory` always enforced at the kept-window boundary. Compaction enforces it at
the *fold* boundary instead: after computing a candidate fold length under the char budget,
`SafeFoldBoundary` grows it forward so the first message left in the pending remainder is never an
orphaned Tool-role message — it pulls the paired Tool result(s) into the fold rather than excluding the
Assistant call that requested them. This can push one pass slightly over `CompactionMaxCharsPerCall`,
but it's a deliberate trade against the alternative (shrinking the candidate fold backward instead):
`pending` is rescanned from the same starting point every pass, so a session whose oldest pending
message happens to be a large Assistant message immediately followed by its own Tool result would
otherwise never be able to fold anything — the candidate would shrink to zero every single time,
permanently stalling compaction for that session while `pending` grows without bound.

## Integration point

`BuildWireMessages` (`src/PersonalAI.Core/Services/Agent/ChatAgentService.cs`) builds the payload sent
to the provider each iteration of `LoopAsync`. Compaction needs an async provider call and a DB write,
so `LoopAsync` calls `EnsureHistoryCompactedAsync(session, history, ct)` right after loading `history`
and before `BuildWireMessages`, gated by `session.TrimHistoryEnabled` (the same toggle that used to
gate `DropOldHistory` — its semantics changed from "drop" to "compact", but the name, DB column, and
`/api/sessions/{id}/settings` field stayed the same to avoid churn). On success it mutates
`session.HistorySummary`/`SummarizedThroughSequenceNumber` in place and persists them via
`IChatSessionRepository.UpdateHistoryCompactionAsync`. `BuildWireMessages` itself stays synchronous —
it just reads whatever state `session` is in when called (`ApplyCompaction`), or falls back to the
legacy drop path if compaction failed this turn (`forceLegacyDrop`, see below).

`LoopAsync` attempts compaction **at most once per turn**, tracked by a local flag set before the
`for` loop — not once per LLM-call iteration. A single turn can make several iterations (each tool call
round-trips through the loop again), and the compaction call is an uncounted, uncapped side call that
doesn't go through `IncrementLlmCallsAsync`/`session.MaxLlmCalls` the way the turn's own calls do;
letting it re-fire on every iteration of a long tool-calling turn could multiply real provider cost far
beyond what `session.MaxLlmCalls` implies. A large backlog still catches up incrementally — just paced
one fold per *turn* rather than one fold per iteration.

## Summarization call

Same session provider/model as the turn itself (`AIProviderRegistry.Resolve(session.ProviderKey)`,
`session.ModelName`) — no separate provider/model selection for the summarizer. A plain
`ChatStreamAsync` call with `tools: []` and `think: false`, fully consumed into a string inside
`EnsureHistoryCompactedAsync` — never yielded as a `TextDeltaEvent`, so it's invisible to the user's
stream. Message shape:

- `system` — instructs the model to produce one updated, terse, bulleted summary preserving the
  user's goals/requests, decisions made, established facts, file paths touched, and tool-call outcomes
  (success/failure + key result); explicitly no prose recap; merge into the previous summary rather
  than appending a new section.
- `user` — `"Previous summary:\n{HistorySummary ?? "(none)"}\n\nMessages to fold in:\n{transcript}"`,
  where the transcript is a flattened `role: content` listing (`FormatForSummary`) — Tool messages
  include their `ToolName` and content capped via the same `CapContent`/`ToolResultMaxChars` used
  elsewhere, not raw. An Assistant message that made a tool call typically has empty `Content` (the
  call itself lives in `ToolCallsJson`, sent to the provider as a separate structured field — see
  `BuildWireMessages`); `FormatForSummary` appends a `[called <name>(<argumentsJson>)]` line for such
  messages so the summarizer can see what the call actually was, not just its Content field (which is
  usually blank).

## Failure handling

The provider call is wrapped in try/catch (`OperationCanceledException` rethrows; anything else is
treated as failure). On failure, `EnsureHistoryCompactedAsync` persists nothing — `HistorySummary`/
`SummarizedThroughSequenceNumber` are left exactly as loaded — and returns `false`. `LoopAsync` reads
that as `compactionFailed` and passes `forceLegacyDrop: true` into `BuildWireMessages` for that turn
only, which falls back to the pre-compaction `DropOldHistory` behavior (fixed kept-window +
"N earlier message(s) omitted" note) so a flaky provider never crashes or silently corrupts a turn.
The next turn simply retries compaction from the same unchanged cursor — no separate retry/backoff
logic.

## Manual compaction

The chat screen's **Compact** button calls `POST /api/sessions/{id}/compact`, which runs
`ChatAgentService.CompactHistoryAsync` through `ChatTurnRunner` like any other turn — so it can't overlap a
running turn (`409`), can be stopped, and a reload re-attaches to it. It is refused while tool approvals are
pending, since folding an unanswered tool call would orphan it.

Unlike the automatic path there is no kept window and no trigger threshold: every message past the cursor
(except context-excluded ones) is folded, in as many `CompactionMaxCharsPerCall`-bounded passes as it takes,
all sharing the same `FoldIntoSummaryAsync` helper (prompt, `SafeFoldBoundary`, summary cap, persistence). A
failed pass throws, surfacing an error event; passes that already succeeded stay persisted. On success it
appends a System message starting `Conversation compacted` with `ExcludeFromContext = true` — never sent to
the model, rendered by the UI as a divider. Raw messages stay in the transcript.

`BuildWireMessages` applies the summary even when `TrimHistoryEnabled` is off, so a manual compaction works
for every session; the toggle only gates *automatic* compaction and tool-result capping.

## Settings

`AgentSettings` (`src/PersonalAI.Core/Models/Settings/AgentSettings.cs`), section `Agent`:

| Setting | Default | Meaning |
|---|---|---|
| `HistoryKeepRecentMessages` | 40 | Size of the always-raw kept window (unchanged from before compaction). |
| `ToolResultMaxChars` | 4000 | Per-message cap for Tool-role content, applied everywhere (wire payload and the summarization transcript alike). |
| `CompactionBatchSize` | 20 | Pending-message-count trigger. |
| `CompactionMaxCharsPerCall` | 24000 | Pending-char-count trigger, and the per-pass fold-in budget. |
| `CompactionMaxSummaryChars` | 8000 | Hard cap on `HistorySummary`'s length. Each pass threads the whole previous summary back into the next prompt with no shrinkage of its own, so without a cap an imperfectly-compressing model could let the summary grow unboundedly over a very long-lived session; exceeding this truncates the newest summary. |

Global-only — no per-session override for the three new settings, consistent with how
`HistoryKeepRecentMessages`/`ToolResultMaxChars` already work. `ChatSession.TrimHistoryEnabled` is the
single on/off switch for the whole feature (compaction and the legacy-fallback drop path together).

## Explicitly not done

No pluggable summarizer abstraction, no per-session summarizer model/provider override, no summary
version history or separate summary table, no per-session override of the three new settings, no
tokenizer-based precision for the char budget (reuses the same char-length heuristic `CapContent`
already uses), no compaction for Skills/Agents.

## Tests

`tests/PersonalAI.Tests/Agent/ChatAgentServiceTests.cs` covers: the no-op fast path when history is
still under the kept window (no extra provider call); a compaction pass replacing dropped messages
with a summary system message; incremental folding across multiple turns, including the prior summary
being threaded into the next compaction call's prompt; the char-budget cap limiting how much a single
pass folds in, requiring multiple turns to catch up on a larger backlog; the char trigger firing before
the count trigger (a handful of large capped tool messages); the failure-fallback path (a failed
summarization call falls back to the legacy drop-with-note behavior without corrupting session state);
`SafeFoldBoundary` extending a fold *forward* past a candidate boundary that would otherwise land on an
orphaned Tool-role message, guaranteeing progress instead of deferring the pass forever; an Assistant
message that only made a tool call (empty `Content`, arguments in `ToolCallsJson`) surfacing its tool
name/arguments in the summarization prompt via `FormatForSummary`; and a multi-iteration tool-calling
turn attempting compaction at most once, not once per iteration.
`FakeAIProvider.ThrowOnNextCall` (`tests/PersonalAI.Tests/Agent/FakeAIProvider.cs`) simulates a
provider failure for the failure-fallback case.
