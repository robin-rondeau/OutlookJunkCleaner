# Junk classification rubric

You — the agent — use this rubric to decide what to do with each unread message in the Junk folder.
**Edit this file as you observe what the agent gets wrong.** It is the only "training" surface; the
rest of the system is structural.

For each message, classify into exactly one of three buckets:

1. **Confident junk** — take the phase-appropriate action (Phase A: `mark_as_read`; Phase B: `delete_from_junk`).
2. **Confident not-junk** — call `move_to_triage` with a one-line reason.
3. **Ambiguous** — call `move_to_triage` with a one-line reason.

When in doubt, move to triage. False positives in confident-junk are far more costly than a noisy Triage folder.

---

## Definite-junk patterns

Mail matching any of these is confident junk.

- **Sender domains (add as you see repeats):**
  - _add domains here, one per line, e.g. `winners-club.example`_
- **Subject patterns:**
  - "You've won …" / "Congratulations, you …" lottery-style subjects
  - "Final notice" / "Your account will be suspended" combined with no recognizable sender
  - Cryptocurrency / investment / forex pitches you have no relationship with
  - Sextortion / blackmail patterns ("I have video of you …")
  - All-caps subjects with money amounts
  - Pharmacy / weight-loss / "miracle cure" pitches
  - Casino / Slots gambling related subjects
- **Body / preview signals:**
  - Asks you to click a link to "verify" / "claim" / "unlock" something with no prior relationship
  - Tracking-pixel-heavy mail with no genuine content
  - **Header signals:**
  - SPF/DKIM both fail in `Authentication-Results` (use `get_message` to check headers if needed)

## Definite-not-junk patterns

Mail matching any of these is confident not-junk. Leave it alone — the user will rescue it manually.

- **Sender domains (add as you see false positives):**
  - opayq.com - these are masked emails generated for personal use which auto-forward to this mailbox
  - maskedmails.com - these are masked emails generated for personal use which auto-forward to this mailbox
  - disengage.info - these are masked emails generated for personal use which auto-forward to this mailbox
  - blurmail.net - these are masked emails generated for personal use which auto-forward to this mailbox
  - moremobileprivacy.org - these are masked emails generated for personal use which auto-forward to this mailbox
  - kijiji.ca
  - linkedin.com
- **Transactional patterns** the user clearly opted into:
  - Receipts / order confirmations / shipping notifications from services they use
  - Two-factor codes, password resets, account verification from real services
  - Calendar invites from people they correspond with
- **Personal mail** (a human writing a real message to them, not a marketing list)

## Ambiguity policy

Triage if **any** of these hold:

- The sender domain is unfamiliar but the message looks transactional (could be a real service).
- It's clearly a marketing list but from a brand the user might actually use.
- You can't confidently classify based on sender + subject + bodyPreview, and fetching the full body
  with `get_message` doesn't resolve the doubt.
- The message looks like a phishing attempt but might also be a legitimate alert from a real service.

When you triage, the `reason` should be one short line — what made it ambiguous. Examples:
- "marketing from brand-name; user may have signed up"
- "looks like delivery notification but unfamiliar domain courier-x.example"
- "suspected phishing pretending to be brand-name; needs human eyes"

## Performance hints

- The bodyPreview + sender + subject + listUnsubscribe header is usually enough. Only call
  `get_message` when you genuinely need the body to decide.
- Don't process more than ~50 messages per run. If `list_junk` returns 50 items, do those, end your
  turn, and the next hourly run will pick up the rest.
- If you've already seen a sender hundreds of times and the rule is solid, the rubric is the right
  place to encode that — ask the user to add the domain to the definite-junk list rather than
  re-deriving it every run.
