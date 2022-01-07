param nameprefix string
param tenantId string
param location string = 'UKSouth'
param environment string
param sku_function_webplan object
param AppInsights_InstrumentationKey string
param etl_nameprefix string
param etl_resource_group string

var name_function_var = '${nameprefix}funccacheflush'
var name_webplan_function_var = '${nameprefix}funcplancacheflush'
var name_storage_function_var = '${nameprefix}fstorecf'
var sku_storage = {
  name: 'Standard_LRS'
}
var tags = {
  Environemnt: toLower(environment) == 'prod' ? 'Production' : environment
  Criticality: toLower(environment) == 'prod' ? 'Tier 1' : toLower(environment) == 'dev' ? 'Tier 2' : 'Tier 3'
  Owner: 'UKHSA - COVID19'
  Org: 'UKHSA'
  Application: 'UK Coronavirus Dashboard'
}

var listener_endpoint = '${service_bus_resource.id}/AuthorizationRules/${sender_auth.name}'


resource service_bus_resource 'Microsoft.ServiceBus/namespaces@2021-06-01-preview' existing = {
  name: '${etl_nameprefix}-events'
  scope: resourceGroup(etl_resource_group)
}

resource sender_auth 'Microsoft.ServiceBus/namespaces/AuthorizationRules@2021-06-01-preview' existing = {
  parent: service_bus_resource
  name: 'Listener'
}


resource name_webplan_function 'Microsoft.Web/serverfarms@2018-02-01' = {
  name: name_webplan_function_var
  location: location
  sku: sku_function_webplan
  properties: {}
  tags: tags
}

resource name_function 'Microsoft.Web/sites@2018-11-01' = {
  name: name_function_var
  location: location
  kind: 'functionapp'
  identity: {
    type: 'SystemAssigned'
  }
  tags: tags
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
          value: 'cache_flusher'
        }
        {
          name: 'SB_SUBSCRIPTION_NAME'
          value: 'flush_all'
        }
        {
          name: 'ServiceBusConnectionString'
          value: 'Endpoint=sb://${service_bus_resource.name}.servicebus.windows.net/;SharedAccessKeyName=${sender_auth.name};SharedAccessKey=${listKeys(listener_endpoint, service_bus_resource.apiVersion).primaryKey}'
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
  tags: tags
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
}
