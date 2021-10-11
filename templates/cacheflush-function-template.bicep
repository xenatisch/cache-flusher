param nameprefix string
param tenantId string
param location string = 'UKSouth'
param environment string
param sku_function_webplan object
param AppInsights_InstrumentationKey string

@secure()
param ServiceBusConnectionString string

var name_function_var = '${nameprefix}funccacheflush'
var name_webplan_function_var = '${nameprefix}funcplancacheflush'
var name_storage_function_var = '${nameprefix}fstorecf'
var sku_storage = {
  name: 'Standard_LRS'
}

resource name_webplan_function 'Microsoft.Web/serverfarms@2018-02-01' = {
  name: name_webplan_function_var
  location: location
  sku: sku_function_webplan
  properties: {}
  tags: {
    'C19-Environment': environment
  }
}

resource name_function 'Microsoft.Web/sites@2018-11-01' = {
  name: name_function_var
  location: location
  kind: 'functionapp'
  identity: {
    type: 'SystemAssigned'
  }
  tags: {
    'C19-Environment': environment
    'C19-Resource-Type': 'function-cacheflush'
  }
  properties: {
    enabled: true
    siteConfig: {
      appSettings: [
        {
          name: 'APPINSIGHTS_INSTRUMENTATIONKEY'
          value: AppInsights_InstrumentationKey
        }
        {
          name: 'AzureWebJobsStorage'
          value: 'DefaultEndpointsProtocol=https;AccountName=${name_storage_function_var};AccountKey=${listKeys(name_storage_function_var, '2019-06-01').keys[0].value}'
        }
        {
          name: 'WEBSITE_CONTENTAZUREFILECONNECTIONSTRING'
          value: 'DefaultEndpointsProtocol=https;AccountName=${name_storage_function_var};AccountKey=${listKeys(name_storage_function_var, '2019-06-01').keys[0].value}'
        }
        {
          name: 'WEBSITE_CONTENTSHARE'
          value: 'fn-c19-cache-flush'
        }
        {
          name: 'FUNCTIONS_WORKER_RUNTIME'
          value: 'dotnet'
        }
        {
          name: 'FUNCTIONS_EXTENSION_VERSION'
          value: '~3'
        }
        {
          name: 'tenantId'
          value: tenantId
        }
        {
          name: 'ENVIRONMENT'
          value: environment
        }
        {
          name: 'SB_TOPIC_NAME'
          value: 'data-despatch'
        }
        {
          name: 'SB_SUBSCRIPTION_NAME'
          value: 'cache-flusher'
        }
        {
          name: 'ServiceBusConnectionString'
          value: ServiceBusConnectionString
        }
        {
          name: 'FLUSH_REPEATS'
          value: '1'
        }
        {
          name: 'REPEAT_DELAY_SECONDS'
          value: '0'
        }
      ]
    }
    serverFarmId: name_webplan_function.id
  }
  dependsOn: [
    name_storage_function
  ]
}

resource name_function_web 'Microsoft.Web/sites/config@2018-11-01' = {
  parent: name_function
  name: 'web'
  properties: {
    cors: {
      allowedOrigins: [
        '*'
      ]
    }
  }
}

resource name_storage_function 'Microsoft.Storage/storageAccounts@2019-06-01' = {
  name: name_storage_function_var
  location: location
  sku: sku_storage
  kind: 'StorageV2'
  properties: {
    minimumTlsVersion: 'TLS1_2'
    allowBlobPublicAccess: true
    supportsHttpsTrafficOnly: true
    encryption: {
      services: {
        file: {
          keyType: 'Account'
          enabled: true
        }
        blob: {
          keyType: 'Account'
          enabled: true
        }
      }
      keySource: 'Microsoft.Storage'
    }
    accessTier: 'Hot'
  }
  tags: {
    'C19-Environment': environment
  }
}
