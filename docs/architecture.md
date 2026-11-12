# CrowdFund AI — Architecture

```
┌─────────────────────────────────────────────────────────────┐
│                     CrowdFund.Web (Blazor)                  │
│  Pages: Home · Campaigns · Donate · Follow My Money · Chat  │
│         Admin Queue/Dossier · Expense Upload · Receipt      │
│  Minimal APIs: /api/donations · /api/campaigns · /api/chat  │
│                /api/admin · /api/ledger · /api/paypal       │
│  SignalR: CampaignHub (flags, transparency, NeedsReview)    │
└───────────────┬─────────────────────────────┬───────────────┘
                │                             │
                ▼                             ▼
┌───────────────────────────┐   ┌─────────────────────────────┐
│ CrowdFund.Ai              │   │ CrowdFund.Infrastructure    │
│ · VerificationCouncil     │   │ · AppDbContext (EF/SQLite)  │
│ · FraudEngine             │   │ · PayPalClient (+ simulate) │
│ · DonorConcierge + Tools  │   │ · LocalFileStore            │
│ · TransparencyScore       │   │ · FakeLlmClient / ILlmClient│
│ · VerificationOrchestrator│   │ · TfIdfEmbeddingService     │
└───────────────┬───────────┘   └──────────────┬──────────────┘
                │                              │
                └──────────────┬───────────────┘
                               ▼
                ┌──────────────────────────────┐
                │ CrowdFund.Core               │
                │ Entities · AllocationEngine  │
                │ LedgerService · DonationSvc  │
                │ ReceiptIntelligence · FMM    │
                └──────────────────────────────┘
```

## Money path (never LLM)
1. Browser → `create-order` → pending `Donation`
2. `capture` **server-side** → PayPal COMPLETED + unique capture ID
3. `AuditLedger` entry (`donation.captured`) in same logical flow
4. Expenses allocated **pro rata in C#** (`donation ÷ raised × category spend`)

## AI path
- All LLM calls via `ILlmClient` (Fake offline; Anthropic-ready config keys)
- Untrusted user/doc text wrapped in `<untrusted_document>`
- Structured trust **sub-scores** from agents; **overall score weighted in C#**
- Concierge tools are read-only except `PrepareDonation` (intent only)

## Projects
| Project | Role |
|---------|------|
| CrowdFund.Web | UI + APIs + Watchdog hosted service |
| CrowdFund.Core | Domain, pure allocation/ledger math |
| CrowdFund.Infrastructure | EF, PayPal, files, LLM, embeddings |
| CrowdFund.Ai | Council, fraud, concierge, transparency |
| CrowdFund.Tests | Unit tests (allocation, ledger, council, concierge) |
| CrowdFund.Eval | Offline harness → `docs/eval-results.md` |
