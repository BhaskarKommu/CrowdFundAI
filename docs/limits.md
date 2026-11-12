# Limits of this system

CrowdFund AI is a **hackathon prototype**. Treat outputs as **risk indicators and assistance**, not legal, medical, or financial guarantees.

## What this system does **not** do
- It does **not** prove a campaign is legitimate or fraudulent.
- It does **not** replace licensed escrow, KYC/AML, or regulated payment institutions.
- The LLM **never** decides final approval; humans do.
- The LLM **never** computes donation totals, allocations, or capture amounts — only C# does.
- FakeLlmClient is used for offline demos; production needs a real model key and monitoring.
- PayPal runs in **sandbox / simulate** mode by default; live money requires real credentials and compliance review.
- Image forensics (ELA, EXIF) and Benford tests are **weak signals** only and need sufficient sample size.
- Multi-language concierge coverage is limited to English, Hindi, and Telugu templates plus model output.
- Hash-chained ledger is **not a blockchain**; it detects local tampering of the append-only table, not distributed consensus.

## Known residual risks
- Prompt injection is mitigated (untrusted wrappers, grounding, score computed in code) but not eliminated for all future models.
- Seeded FRAUD-DEMO behaviour is partially rule-based so the demo is reliable offline.
- File malware scanning is a documented hook point only — not a full AV pipeline.
- Guest donations and email hashing are simplified for the prototype.

## Responsible use
- Always show disclaimers on trust scores and flags.
- Creators must be able to respond to flags.
- Donors should still apply independent judgment.


## Legal & compliance surfaces
- Terms, Privacy, Refund, Acceptable Use, AI Disclaimer under `/legal/*`
- Immutable `SecurityAuditEvents` log (actor, IP, action, entity) with CSV export
- Application rate limiting + security headers; put a cloud WAF (Cloudflare/Azure Front Door/AWS WAF) in front for production
