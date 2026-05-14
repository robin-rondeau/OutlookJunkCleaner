# Junk classification rubric

**Edit this file as you observe what the classifier gets wrong.** It is the natural-language
"training" surface; structured sender allow/deny lists live in `senders.json` and are presented
above this rubric.

---

## Definite-junk patterns

Mail matching any of these is `confident_junk`. (Sender-domain matches against the known-junk
list above also qualify on their own; `reason` may be brief, e.g. "in known-junk list".)

- **Subject patterns:**
  - "You've won …" / "Congratulations, you …" lottery-style subjects
  - "Final notice" / "Your account will be suspended" combined with no recognizable sender
  - Cryptocurrency / investment / forex pitches you have no relationship with
  - Sextortion / blackmail patterns ("I have video of you …")
  - All-caps subjects with money amounts
  - Pharmacy / weight-loss / "miracle cure" pitches
  - Casino / Slots gambling related subjects
  - Timeshare related subjects
- **Body / preview signals:**
  - Asks you to click a link to "verify" / "claim" / "unlock" something with no prior relationship
  - Tracking-pixel-heavy mail with no genuine content
  - Any visible-text / href host mismatch hint surfaced by the sanitiser (likely phishing)
- **Header signals:**
  - SPF/DKIM both fail in `Authentication-Results`
  - `message-id:` contains a literal template placeholder like `%LETTERS_UPPER-5%`,
    `%FIRSTNAME%`, `{{token}}`, etc. This is a leaked mail-merge variable from the
    spammer's pipeline; legitimate senders never emit unfilled placeholders. Strong
    standalone `confident_junk` signal.
  - `microsoft-spam-confidence:` is `5` or higher. This is Exchange Online Protection's
    upstream verdict (the SCL header); it scores 0–9 where 5–6 is "spam" and 7–9 is
    "high-confidence spam". When SCL ≥ 5, **do not downgrade to `ambiguous`** without
    a positive trusted-sender signal — i.e. the From-domain matches an entry on the
    trusted list above, or the body is unambiguously transactional from a service the
    user has clearly opted into. Absent such a rescue signal, treat SCL ≥ 5 as
    `confident_junk` and name "EOP SCL=N" in the reason. SCL of `-1` means EOP skipped
    filtering (trusted internal mail) and is not a junk signal; missing / `<none>`
    means no SCL header was present and carries no weight either way.
- **Affiliate / drive-by marketing patterns** (any TWO together = `confident_junk`; any one
  alone = `ambiguous`). ESP-relay caveat: `sendgrid.net`, `mailgun.org`, `amazonses.com` and
  similar shared relays don't qualify for the sender-domain signals on their own.
  - **Brand-claim ↔ sender-domain mismatch.** From-name claims a brand (e.g. "Brinks Home",
    "Jacuzzi Bath Remodel") but `sender-domain:` doesn't contain it and isn't a branded ESP
    subdomain like `em.brand.com`.
  - **Affiliate-marketer giveaway tokens in the From-name.** "Ad Partner", "Partner", "Promo",
    "Promotions", "Marketing Partner", "Ad Network" attached to a brand name. Legit brands
    don't describe themselves as their own promotion partner.
  - **Transactional alias hosting promotional content.** Local-part is `alert@`, `notice@`,
    `no-reply@`, `service@`, `update@`, but content is promotional (offers, sale pitches,
    "limited time") rather than transactional (receipts, 2FA, password resets).
  - **Sender-domain has no apparent business identity.** Meaningless pronounceable string
    (e.g. `adinstor.com`, `desponso.com`, `secretiri.com`) unrelated to the claimed brand, or
    throwaway TLD (`.click`, `.top`, `.shop`, `.life`, `.icu`, `.xyz`).
  - **Single-target image cluster.** Three or more images with distinct product alt-text but
    most or all `links` hrefs resolve to the same host. Real catalog mail has distinct
    destinations per product.

  Tie-breakers (don't count toward the two-of; mention in `reason` only):
  - `list-unsubscribe: <none>` AND no unsubscribe link in body.
  - `reply-to:` domain differs from `sender-domain:`.

## Definite-not-junk patterns

Mail from any sender domain in the trusted list above should not be classified as
`confident_junk` on signal patterns alone. If anything still looks borderline, choose
`ambiguous` and let the user decide in Triage. Beyond the trusted-domain list:

- **Transactional patterns** the user clearly opted into:
  - Receipts / order confirmations / shipping notifications from services they use
  - Two-factor codes, password resets, account verification from real services
  - Calendar invites from people they correspond with
- **Personal mail** (a human writing a real message, not a marketing list)

## Ambiguity policy

Choose `ambiguous` if **any** of these hold:

- The sender domain is unfamiliar but the message looks transactional (could be a real service).
- It's clearly a marketing list but from a brand the user might actually use.
- You can't confidently classify based on the available signals (sender + subject +
  list-unsubscribe + body + headers + extracted images and links).
- The message looks like a phishing attempt but might also be a legitimate alert from a real service.

Calibration examples for the `reason`:
- "marketing from brand-name; user may have signed up"
- "looks like delivery notification but unfamiliar domain courier-x.example"
- "suspected phishing pretending to be brand-name; needs human eyes"
