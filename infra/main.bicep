@description('Deployment environment: dev, staging, or prod')
@allowed([
  'dev'
  'staging'
  'prod'
])
param environment string

@description('Azure region for all resources')
param location string = resourceGroup().location

@description('PostgreSQL administrator login')
param postgresAdminLogin string = 'strataadmin'

@description('PostgreSQL administrator password')
@secure()
param postgresAdminPassword string

module appInsights 'modules/appinsights.bicep' = {
  name: 'appInsightsDeploy'
  params: {
    location: location
    environment: environment
  }
}

module storage 'modules/storage.bicep' = {
  name: 'storageDeploy'
  params: {
    location: location
    environment: environment
  }
}

module functionsInit 'modules/functions.bicep' = {
  name: 'functionsDeploy'
  params: {
    location: location
    environment: environment
    storageAccountName: storage.outputs.storageAccountName
    appInsightsConnectionString: appInsights.outputs.connectionString
    keyVaultUri: keyVault.outputs.keyVaultUri
    connectionStringSecretUri: postgres.outputs.connectionStringSecretUri
  }
  dependsOn: [
    keyVault
    postgres
  ]
}

module keyVault 'modules/keyvault.bicep' = {
  name: 'keyVaultDeploy'
  params: {
    location: location
    environment: environment
    functionsAppPrincipalId: functionsInit.outputs.principalId
  }
}

module postgres 'modules/postgres.bicep' = {
  name: 'postgresDeploy'
  params: {
    location: location
    environment: environment
    administratorLogin: postgresAdminLogin
    administratorLoginPassword: postgresAdminPassword
    keyVaultName: keyVault.outputs.keyVaultName
  }
  dependsOn: [
    keyVault
  ]
}

module swa 'modules/swa.bicep' = {
  name: 'swaDeploy'
  params: {
    location: location
    environment: environment
  }
}

output functionAppName string = functionsInit.outputs.functionAppName
output functionAppHostname string = functionsInit.outputs.functionAppHostname
output keyVaultName string = keyVault.outputs.keyVaultName
output postgresServerName string = postgres.outputs.serverName
output staticWebAppHostname string = swa.outputs.defaultHostname
output appInsightsConnectionString string = appInsights.outputs.connectionString
