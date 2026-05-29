# fn-strata-reports

Azure Functions backend for StrataReport AI — .NET 9 isolated worker model.

## Prerequisites

- [Azure CLI](https://learn.microsoft.com/en-us/cli/azure/install-azure-cli) >= 2.60
- Azure subscription with Owner or Contributor + User Access Administrator roles
- [Bicep CLI](https://learn.microsoft.com/en-us/azure/azure-resource-manager/bicep/install) (installed automatically via `az bicep install`)
- .NET 9 SDK

## One-command deploy

```bash
az bicep install

ENVIRONMENT=dev   # dev | staging | prod
RESOURCE_GROUP=rg-strata-${ENVIRONMENT}
LOCATION=eastus

az group create \
  --name "$RESOURCE_GROUP" \
  --location "$LOCATION"

az deployment group create \
  --resource-group "$RESOURCE_GROUP" \
  --template-file infra/main.bicep \
  --parameters environment="$ENVIRONMENT" \
               postgresAdminPassword="$(openssl rand -base64 32)"
```

> Replace `$ENVIRONMENT` with `dev`, `staging`, or `prod`. Each environment deploys into its own isolated resource group.

## Environments

| Environment | Resource Group | Postgres SKU | Functions Plan |
|-------------|---------------|--------------|----------------|
| dev | rg-strata-dev | B1ms | Consumption (Y1) |
| staging | rg-strata-staging | B1ms | Consumption (Y1) |
| prod | rg-strata-prod | B2s | Consumption (Y1) |

## Infrastructure overview

All resources are defined in `infra/`:

| File | Resource |
|------|----------|
| `infra/main.bicep` | Entry point — wires all modules |
| `infra/modules/appinsights.bicep` | Application Insights + Log Analytics workspace |
| `infra/modules/functions.bicep` | Azure Functions App (isolated worker, .NET 9) |
| `infra/modules/keyvault.bicep` | Key Vault with RBAC; grants Functions `Key Vault Secrets User` |
| `infra/modules/postgres.bicep` | PostgreSQL Flexible Server; stores connection string in Key Vault |
| `infra/modules/storage.bicep` | Blob Storage (hot tier) with 90-day lifecycle to cool |
| `infra/modules/swa.bicep` | Azure Static Web Apps (Standard plan) |

## Secrets management

All secrets are stored in Key Vault. The Functions App references them via the `@Microsoft.KeyVault()` syntax — no secrets appear in source code or environment variable literals.

| Secret Name | Description |
|-------------|-------------|
| `ConnectionStrings--Database` | PostgreSQL connection string |
| `PostgresAdminPassword` | Postgres admin password |

## CI/CD

| Workflow | Trigger | Description |
|----------|---------|-------------|
| `.github/workflows/deploy-api.yml` | Push to `main` | Builds & deploys Functions App through dev → staging → prod |
| `.github/workflows/deploy-web.yml` | Push to `main` | Builds & deploys React/Vite SPA to Static Web Apps through dev → staging → prod |

### Required GitHub secrets per environment

Configure these in **Settings → Environments** for each of `dev`, `staging`, and `prod`:

| Secret | Description |
|--------|-------------|
| `AZURE_CLIENT_ID` | Service principal / managed identity client ID (OIDC) |
| `AZURE_TENANT_ID` | Azure AD tenant ID |
| `AZURE_SUBSCRIPTION_ID` | Azure subscription ID |
| `AZURE_FUNCTION_APP_NAME` | Name of the deployed Function App (e.g. `func-strata-dev`) |
| `AZURE_STATIC_WEB_APPS_API_TOKEN` | SWA deployment token (from `swa.outputs.deploymentToken`) |
| `VITE_API_BASE_URL` | Base URL of the Functions App for the SPA |

### Retrieving SWA deployment token after infra deploy

```bash
az deployment group show \
  --resource-group rg-strata-dev \
  --name swaDeploy \
  --query properties.outputs.deploymentToken.value \
  --output tsv
```

## Third-party licenses

| Package | License |
|---------|---------|
| [QuestPDF](https://www.questpdf.com/) | QuestPDF Community License (MIT) — applicable for organisations with < $1M USD annual gross revenue. See [questpdf.com/license](https://www.questpdf.com/license.html) for full terms. |
| [ScottPlot](https://scottplot.net/) | MIT License |

## Local development

```bash
dotnet restore
dotnet build StrataReports.Functions.csproj --configuration Release
```

Set the following in `local.settings.json` (not committed):

```json
{
  "IsEncrypted": false,
  "Values": {
    "AzureWebJobsStorage": "UseDevelopmentStorage=true",
    "FUNCTIONS_WORKER_RUNTIME": "dotnet-isolated",
    "ConnectionStrings:Database": "<local-postgres-connection-string>"
  }
}
```
