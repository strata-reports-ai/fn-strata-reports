param location string
param environment string
param administratorLogin string
@secure()
param administratorLoginPassword string
param keyVaultName string

var serverName = 'psql-strata-${environment}'
var skuName = environment == 'prod' ? 'Standard_B2s' : 'Standard_B1ms'
var storageSizeGB = environment == 'prod' ? 32 : 20

resource postgresServer 'Microsoft.DBforPostgreSQL/flexibleServers@2023-12-01-preview' = {
  name: serverName
  location: location
  sku: {
    name: skuName
    tier: 'Burstable'
  }
  properties: {
    administratorLogin: administratorLogin
    administratorLoginPassword: administratorLoginPassword
    version: '16'
    storage: {
      storageSizeGB: storageSizeGB
    }
    backup: {
      backupRetentionDays: environment == 'prod' ? 7 : 3
      geoRedundantBackup: 'Disabled'
    }
    highAvailability: {
      mode: 'Disabled'
    }
  }
}

resource pgStatStatementsConfig 'Microsoft.DBforPostgreSQL/flexibleServers/configurations@2023-12-01-preview' = {
  parent: postgresServer
  name: 'azure.extensions'
  properties: {
    value: 'pg_stat_statements'
    source: 'user-override'
  }
}

var connectionString = 'Host=${postgresServer.properties.fullyQualifiedDomainName};Database=stratareports;Username=${administratorLogin};Password=${administratorLoginPassword};SSL Mode=Require;Trust Server Certificate=True'

resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' existing = {
  name: keyVaultName
}

resource dbConnectionStringSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: keyVault
  name: 'ConnectionStrings--Database'
  properties: {
    value: connectionString
  }
}

resource dbPasswordSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: keyVault
  name: 'PostgresAdminPassword'
  properties: {
    value: administratorLoginPassword
  }
}

output serverFqdn string = postgresServer.properties.fullyQualifiedDomainName
output serverName string = postgresServer.name
output connectionStringSecretUri string = dbConnectionStringSecret.properties.secretUri
