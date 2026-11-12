# Operations: secrets, backups, observability

## Secrets & configuration
**Never commit secrets.** `appsettings.json` keeps empty placeholders.

Priority order:
1. **Environment variables** (`ConnectionStrings__Default`, `PayPal__ClientSecret`, `Llm__ApiKey`, …)
2. **User secrets** (Development): `dotnet user-secrets set "PayPal:ClientSecret" "..." --project CrowdFund.Web`
3. **Azure Key Vault** when `AzureKeyVault:Uri` is set (DefaultAzureCredential)

See `.env.example` and `CrowdFund.Web/appsettings.Example.json`.

### Rotate PayPal / LLM keys
1. Create new credentials in PayPal / LLM provider  
2. Update Key Vault or env vars  
3. Restart app instances  
4. Revoke old credentials  
5. Verify sandbox/live capture in `/health` + a test donation  

## Backups & restore
### Application archive (files + SQL reachability metadata)
```bash
# Admin authenticated call, or trigger from ops box:
curl -X POST https://your-host/api/admin/backup
```
Archives land in `Backup:Root` (default `App_Data/backups`).

### Native SQL Server full backup
```bash
export SQLCMDSERVER=localhost,1433 SQLCMDUSER=sa SQLCMDPASSWORD='...' DB_NAME=CrowdFundAI
./scripts/backup-sql.sh
```

### Restore
```bash
export BAK_PATH=/path/to/CrowdFundAI_YYYYMMDD_HHMMSS.bak
./scripts/restore-sql.sh
./scripts/restore-files.sh ./App_Data/backups/crowdfund-backup-....zip ./App_Data/uploads
```
Test restore on a staging database at least once before go-live.

## Observability
| Endpoint | Purpose |
|----------|---------|
| `GET /health` | Liveness |
| `GET /health/ready` | SQL DbContext check |
| `GET /api/ops/metrics` | Capture / ledger / fraud counters |

- **Serilog** → console + rolling files under `App_Data/logs/`
- **Alerts** → critical/warning logs + optional `Observability:AlertWebhookUrl`
- Thresholds: failed captures & fraud flag spikes (background checker every 2 minutes)

Instrumentations:
- Capture success/failure
- Ledger verify break
- Watchdog new fraud flags


## Object storage & virus scan
Set `ObjectStorage:Provider` to `R2` or `S3` and supply Endpoint, Bucket, AccessKey, SecretKey, PublicBaseUrl (CDN).
Optional ClamAV: `ObjectStorage:ClamAvHost` + Port.

## Partner API
1. Admin creates partner → receives client_id + client_secret  
2. `POST /api/oauth/token` with grant_type=client_credentials  
3. `Authorization: Bearer {access_token}` on `/api/partner/v1/*`  
Tenant-scoped partners only see their `TenantId` campaigns.

## CDN
Point CDN at static origin; do not cache HTML auth routes. Static responses include `Cache-Control: public,max-age=604800`.


## White-label Trust API
1. Create Tenant (branding) and Partner with TenantId + scope `trust:write`
2. Partner obtains token via `/api/oauth/token`
3. `POST /api/trust/v1/campaigns` with `externalRef`
4. Upload documents; `POST .../evaluate`
5. Embed `.../badge.svg` on the external site
6. Register `trust.updated` webhooks


## Enterprise Transparency-as-a-Service
1. Create Tenant (branding) at `/admin/tenants`
2. `POST /api/admin/enterprise` `{ "tenantId": "...", "tier": "Enterprise", "reportCadence": "weekly" }`
3. Attach campaigns via `Campaign.TenantId`
4. NGO admin opens `/enterprise/{tenantId}`
5. Generate portfolio HTML report, compliance manifest, bulk-verify queue
6. Integrators: tenant-scoped partner token → `GET /api/enterprise/v1/portfolio`


## PayPal gateway
- Admin UI: `/admin/payments`
- Status (no secrets): `GET /api/admin/payments/gateway`
- Test OAuth: `POST /api/admin/payments/gateway/test`
- Merchants: configure `PayPal__Merchants__0__Key` etc. or JSON array in config
- Token service: `PayPalAccountService` (shared by Orders + Payouts)
