# CrowdFund AI

**Trust is the product.** AI agents audit campaigns and expenses; a tamper-evident ledger proves the audit trail. PayPal collects donations. **Follow My Money** shows donors exactly how funds were used.

**PayPal AI Hackathon** · Deadline 13 Nov 2026, 1:30 AM IST · Target submit by evening 12 Nov IST.

## One-line pitch
Transparent AI-powered fundraising: multi-agent verification, server-side PayPal capture, and pro-rata allocation computed only in C#.

## Quick start
```bash
# .NET 8 SDK required
cd CrowdFund.Web
dotnet run

### Database (Microsoft SQL Server)
Default connection string targets **SQL Server** (not SQLite):

```
Server=localhost,1433;Database=CrowdFundAI;User Id=sa;Password=Your_strong_Password123;TrustServerCertificate=True;Encrypt=True
```

Update `ConnectionStrings:Default` in `appsettings.json` / user-secrets. Ensure SQL Server is running, then:

```bash
dotnet ef database update --project CrowdFund.Infrastructure --startup-project CrowdFund.Web
# or rely on EnsureCreated in Development
```
```
Open the printed HTTPS URL.

| Role | Email | Password |
| --- | --- | --- |
| Admin | admin@crowdfundai.demo | Admin123! |
| Auditor | auditor@crowdfundai.demo | Auditor123! |
| Creator | creator1@crowdfundai.demo | Creator123! |
| Donor | donor1@crowdfundai.demo | Donor123! |

PayPal defaults to **simulate mode** (`PayPal:Simulate = true`). Set ClientId/Secret and `Simulate: false` for real sandbox.

## Solution layout
```
CrowdFund.Web              Blazor UI + minimal APIs + Watchdog
CrowdFund.Core             Entities, AllocationEngine, LedgerService
CrowdFund.Infrastructure   EF Core, PayPal, FileStore, FakeLlmClient
CrowdFund.Ai               Council, Fraud, Concierge, Transparency
CrowdFund.Tests            Unit tests
CrowdFund.Eval             Offline evaluation harness
docs/                      Architecture, limits, eval results
```

See [docs/architecture.md](docs/architecture.md).

## Build plan status
| Step | Status |
|------|--------|
| 1 Scaffold & foundation | ✅ |
| 2 PayPal + audit ledger | ✅ |
| 3 Expenses + Follow My Money | ✅ |
| 4 AI Council + Fraud Engine | ✅ |
| 5 Concierge + Watchdog | ✅ |
| 6 Polish, eval, docs | ✅ |

**Deferred (cut line):** A8 Smart matching, A9 Auto impact reports, A10 Milestone Payouts.

## Payments dashboard
Admin `/admin/payments`: metrics (transactions, volume, refunds, subscriptions, payouts, disputes), enable/disable PayPal, sandbox/live preference, webhook URL, notification toggles, reports, links to transactions/refunds/webhooks. Secrets remain in env/user-secrets only.

## Invoicing & payment links
Create/send/cancel/remind invoices (PayPal Invoicing API) · record payments · payment links with shareable `/pay/{slug}` URLs · track link transactions. UI: `/invoices`.

## Customer management
Register/associate customers, PayPal payer/customer ids, billing address, payment & subscription & refund history, vault saved methods, admin search. **Never stores raw card numbers, CVV, or payment credentials** — only opaque PayPal vault/setup tokens and optional last-4 display metadata. UI: `/admin/customers` · APIs: `/api/customers/*` and `/api/admin/customers/*`.

## Transaction management
Admin UI `/admin/transactions` · API `/api/admin/transactions` with search, status/date/customer filters, detail (refunds + webhooks), CSV/Excel export, reconciliation summary.

## PayPal webhooks
- Endpoint: `POST /api/paypal/webhook` (alias `/api/webhooks/paypal`)
- Signature verification, dedupe by event id, event log, failed retry (`POST /api/admin/webhooks/retry-failed`)
- Events: capture completed/pending/denied/refunded, authorization created/voided, checkout approved/completed, subscription lifecycle, disputes
- Admin logs: `GET /api/admin/webhooks` · registration guide: `GET /api/admin/webhooks/registration-guide`
- Set `PayPal__WebhookId` after creating the webhook in PayPal Dashboard

## Subscription management (PayPal Billing)
Plans with weekly/monthly/yearly cycles, trial days, setup fees · start/activate/cancel/suspend/reactivate · upgrade/downgrade (revise) · renew · failed payment + retry · expire · billing history · status.
APIs under `/api/subscriptions/*` · SignalR `SubscriptionStatusChanged`.

## Refund management
- Request: `POST /api/donations/{id}/refunds/request` (reason codes, full/partial)
- History: `GET /api/refunds/{id}/history`
- Process (PayPal): `POST /api/admin/refunds/{id}/process`
- Confirm: `POST /api/admin/refunds/{id}/confirm`
- Reasons: `GET /api/refunds/reasons`
- Webhook: `PAYMENT.CAPTURE.REFUNDED` → auto status sync
- Notifications: SignalR `RefundStatusChanged`
- Admin UI: `/admin/refunds`

## Payment processing
Lifecycle under `/api/payments/orders`: create (CAPTURE/AUTHORIZE + idempotency), authorize, capture full/partial, cancel/void, mark-failed, status (+ SignalR PaymentStatusChanged), by-reference. Unique CF- transaction ids. Status UI: `/payments/status/{id}`.

## PayPal payment methods
| Method | Implementation |
|--------|----------------|
| PayPal Checkout | Orders API create + capture |
| Express Checkout | Smart Buttons wallet path (modern Express) |
| Smart Payment Buttons | wwwroot/paypal-smart.js + Donate page |
| Debit/Credit cards | Smart Buttons card funding |
| Guest checkout | Enabled in checkout config |
| Pay Later | enable-funding=paylater |
| PayPal Wallet | Default funding source |
| One-time payments | Create-order + capture |
| Recurring / automatic | Billing Subscriptions API + RecurringDonation |
| Vault / saved methods | /api/payments/vault/setup-token |
| Regional APMs | enable-funding when eligible |

Config: PayPal:Checkout · GET /api/payments/methods · GET /api/payments/checkout-config

## PayPal account integration
| Capability | Status |
|------------|--------|
| Business account via REST Client ID/Secret | Yes |
| Sandbox & Live (`PayPal__Mode`) | Yes |
| Secure credentials (env / .env / user-secrets / Key Vault) | Yes |
| OAuth 2.0 client_credentials | Yes |
| Access token cache + refresh before expiry | Yes |
| Multi-merchant accounts (`PayPal:Merchants`) | Yes |
| Gateway settings UI | `/admin/payments` |
| Connection test | `POST /api/admin/payments/gateway/test` |
| Public config (ClientId only) | `GET /api/payments/gateway/public-config` |

## PayPal sandbox setup
1. Copy `.env.example` → `.env` (gitignored) and set `PayPal__ClientId` / `PayPal__ClientSecret`
2. Set `PayPal__Mode=sandbox` and `PayPal__Simulate=false`
3. Align `PayPal__ReturnUrl` / `PayPal__CancelUrl` with your public HTTPS host
4. Optional: `dotnet user-secrets set "PayPal:ClientSecret" "..."` under CrowdFund.Web
5. On startup logs: `PayPal mode: sandbox, simulate=False, clientIdConfigured=True`
6. **Never commit secrets.** Client Secret must not be in source control.

## Secrets, backups, observability
See [docs/operations.md](docs/operations.md).

- Config: environment variables / user-secrets / optional Azure Key Vault  
- Backups: `POST /api/admin/backup` + `scripts/backup-sql.sh` / restore scripts  
- Health: `/health`, `/health/ready` · Metrics: `/api/ops/metrics` · Serilog files in `App_Data/logs`

## Security & legal
- Legal: `/legal/terms` · `/legal/privacy` · `/legal/refund` · `/legal/acceptable-use` · `/legal/ai`
- Immutable audit: `/admin/audit` + `GET /api/admin/audit/export`
- Rate limits on capture, chat, upload, KYC, payouts; security headers middleware
- Production: place a real WAF/CDN in front; do not rely on app headers alone

## Go-live modules (KYC / AML / Payouts / Tax)
- **KYC**: `/kyc` — creator profile, tiered limits (Unverified → Basic → Enhanced), document upload, admin queue at `/admin/compliance`
- **AML**: local demo sanctions list on profile save, large-context screens, payout block on Match (swap provider for production)
- **Payouts**: `/payouts` — request by campaign; fee by tier; admin approve triggers PayPal Payouts (simulate by default)
- **Tax receipts**: `GET /api/donations/{id}/tax-receipt` — 80G / 501(c)(3) fields when nonprofit profile is set; print-to-PDF

Tier limits (configurable in `PlatformLimits`):
| Tier | Max goal | Max payout | Fee |
|------|----------|------------|-----|
| Unverified | 1,000 | 0 | — |
| Basic | 25,000 | 5,000 | 5% |
| Enhanced | 500,000 | 100,000 | 3% |

## Trust & transparency
- **Creator risk graph:** shared docs/vendors/story/email · `/admin/risk-graph` · rebuild API
- **Flags + right of reply:** `/campaigns/{id}/flags` · Public / DonorsOnly / Internal
- **Auditor role:** `auditor@crowdfundai.demo` / `Auditor123!` · `/auditor` read-only
- **Scheduled re-verification:** every 6h full Council + notifications + risk rebuild
- **Transparency log:** `/transparency-log` · publish ledger roots · verify chain API

## Trust & transparency (badges & evidence)
- **Public badge:** `/api/public/campaigns/{id}/badge.svg` + `.json` (trust band, verified spend %, ledger head hash)
- **Live transparency:** `/campaigns/{id}/transparency` — history + “what changed”
- **Evidence pack:** `/expenses/{id}/evidence` — files, SHA-256, AI extraction, review status
- **Donor receipt challenge:** Matches / Does not match / Unsure → mismatches open fraud flags

## Key features
- **PayPal**: create-order → **server capture only** → unique capture ID → ledger entry  
- **Follow My Money**: pro-rata allocation (`donation ÷ raised × category spend`), Sankey-style UI, impact receipt  
- **Multi-agent Council**: sub-scores from agents; **overall score in C#**  
- **Fraud engine**: duplicate SHA, outliers, timeline, velocity, Benford (n≥30) — never concludes “fraud”  
- **Concierge**: tool-using, grounded, EN/HI/TE; PrepareDonation never moves money  
- **Watchdog**: transparency score + SignalR alerts  

## Evaluation
```bash
dotnet run

### Database (Microsoft SQL Server)
Default connection string targets **SQL Server** (not SQLite):

```
Server=localhost,1433;Database=CrowdFundAI;User Id=sa;Password=Your_strong_Password123;TrustServerCertificate=True;Encrypt=True
```

Update `ConnectionStrings:Default` in `appsettings.json` / user-secrets. Ensure SQL Server is running, then:

```bash
dotnet ef database update --project CrowdFund.Infrastructure --startup-project CrowdFund.Web
# or rely on EnsureCreated in Development
``` --project CrowdFund.Eval
```
Results: [docs/eval-results.md](docs/eval-results.md)

## AI-use disclosure
- Offline/demo path uses **FakeLlmClient** (canned structured JSON).  
- Configuration keys `Llm:Model` / `Llm:ApiKey` are reserved for Anthropic Messages API.  
- Untrusted campaign/receipt text is wrapped in `<untrusted_document>`; agents are instructed to treat it as data.  
- Trust **overall** score and all **money math** are deterministic C#.  
- Concierge answers are grounding-checked against tool payloads.  

## Limits of this system
See [docs/limits.md](docs/limits.md). Summary: risk indicators only; not legal/financial advice; not a blockchain; sandbox payments; humans approve campaigns.

## Demo script (≤3 min)
1. Problem: donors cannot verify use of funds  
2. Submit/open **FRAUD-DEMO** → Run Council → High flags + low trust  
3. Approve **clean** Aarav campaign  
4. Donate (simulated PayPal) → ledger entry + receipt  
5. Follow My Money + Concierge (optional Hindi/Telugu)  
6. Admin **break-ledger** → verify fails; show eval results  

`POST /api/admin/campaigns/{id}/break-ledger` then `GET /api/ledger/{id}/verify`

## License
MIT — see [LICENSE](LICENSE)


## Publish to GitHub
```bash
cd CrowdFundAI
git init
git add .
git commit -m "CrowdFund AI — PayPal AI Hackathon submission"
git branch -M main
git remote add origin https://github.com/YOUR_USER/CrowdFundAI.git
git push -u origin main
```
Never push real API keys. `PayPal:Simulate` stays `true` until sandbox credentials are configured via user-secrets.
