// One Foundry (AIServices) account + project + model deployments from ../models.json.
param name string
param location string
param tags object = {}
param projectName string
param principalId string
param principalType string
param assignRbac bool
param disableLocalAuth bool

@description('Map of feature flag name -> bool, matched against models.json "enabledBy".')
param flags object

param embeddingModel string
param claudeOrganizationName string
param claudeCountryCode string
param claudeIndustry string

var catalog = loadJsonContent('../models.json').deployments
var enabled = filter(catalog, d => d.enabledBy == 'always' || (flags[?d.enabledBy] ?? false))
var deploymentsToCreate = map(enabled, d => d.role == 'embedding' ? union(d, { model: embeddingModel }) : d)

resource account 'Microsoft.CognitiveServices/accounts@2025-10-01-preview' = {
  name: name
  location: location
  tags: tags
  kind: 'AIServices'
  sku: {
    name: 'S0'
  }
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    customSubDomainName: name
    allowProjectManagement: true
    publicNetworkAccess: 'Enabled'
    disableLocalAuth: disableLocalAuth
    networkAcls: {
      defaultAction: 'Allow'
    }
  }
}

resource project 'Microsoft.CognitiveServices/accounts/projects@2025-10-01-preview' = {
  parent: account
  name: projectName
  location: location
  tags: tags
  identity: {
    type: 'SystemAssigned'
  }
  properties: {}
}

module roles 'role-assignments.bicep' = if (assignRbac && !empty(principalId)) {
  name: 'foundry-role-assignments'
  params: {
    accountName: account.name
    principalId: principalId
    principalType: principalType
  }
}

// Foundry serializes deployment operations on one account; @batchSize(1) avoids 409 conflicts.
// Depending on the role assignment also gives RBAC a head start on propagation.
@batchSize(1)
resource deployments 'Microsoft.CognitiveServices/accounts/deployments@2025-10-01-preview' = [
  for d in deploymentsToCreate: {
    parent: account
    name: d.name
    sku: {
      name: d.sku
      capacity: d.capacity
    }
    properties: union(
      {
        model: {
          format: d.format
          name: d.model
          version: d.version
        }
        versionUpgradeOption: 'OnceNewDefaultVersionAvailable'
        raiPolicyName: 'Microsoft.DefaultV2'
      },
      d.format == 'Anthropic'
        ? {
            modelProviderData: {
              organizationName: claudeOrganizationName
              countryCode: claudeCountryCode
              industry: claudeIndustry
            }
          }
        : {}
    )
    dependsOn: [
      project
      roles
    ]
  }
]

output name string = account.name
output id string = account.id
output endpoint string = 'https://${account.name}.services.ai.azure.com/'
output projectEndpoint string = 'https://${account.name}.services.ai.azure.com/api/projects/${project.name}'
output deployments object = toObject(deploymentsToCreate, d => d.role, d => d.name)
