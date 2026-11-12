# Contributing / publishing this repo

1. Do **not** commit PayPal secrets or LLM API keys. Use user-secrets or environment variables:
   ```bash
   dotnet user-secrets set "PayPal:ClientId" "..." --project CrowdFund.Web
   dotnet user-secrets set "PayPal:ClientSecret" "..." --project CrowdFund.Web
   dotnet user-secrets set "Llm:ApiKey" "..." --project CrowdFund.Web
   ```
2. Run tests: `dotnet test`
3. Run eval harness: `dotnet run --project CrowdFund.Eval`
4. Keep MIT license and AI-use disclosure in the README.
