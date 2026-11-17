// Media Studio Azure footprint: one resource group, one Foundry (AIServices) account, one project,
// and the pay-per-use model deployments listed in models.json. Entra ID only by default.
// Usage: `azd up` (see docs). Teardown: `azd down --purge`.
targetScope = 'subscription'

@minLength(1)
@maxLength(64)
@description('azd environment name; used for the resource group name and the resource token.')
param environmentName string

@minLength(1)
@description('Azure region for all resources. Run infra/scripts/Find-FoundryRegion.ps1 to pick one.')
param location string

@description('Object ID of the user or service principal that gets data-plane access. Empty skips role assignments.')
param principalId string = ''

@allowed([
  'User'
  'ServicePrincipal'
  'Group'
])
@description('Type of principalId: User for local `azd up`, ServicePrincipal for CI.')
param principalType string = 'User'

@description('Deploy the optional OpenAI audio models (gpt-4o-mini-transcribe, gpt-4o-mini-tts). MAI speech needs no deployment.')
param deployOpenAIAudio bool = false

@description('Deploy Mistral OCR (role "ocr").')
param deployMistralOcr bool = true

@description('Deploy Claude (role "claude"). Requires the Marketplace attestation parameters below.')
param deployClaude bool = false

@description('Disable API keys so only Microsoft Entra ID works (recommended).')
param disableLocalAuth bool = true

@description('Assign data-plane roles to principalId.')
param assignRbac bool = true

@description('Content Understanding defaults are configured by the postprovision hook when true.')
param enableContentUnderstanding bool = true

@allowed([
  'text-embedding-3-small'
  'text-embedding-3-large'
])
@description('Embedding model for the "embedding" deployment (also used by Content Understanding). Switch to -large if an analyzer requires it.')
param cuEmbeddingModel string = 'text-embedding-3-small'

@description('Claude Marketplace attestation: your organization name. Required when deployClaude is true.')
param claudeOrganizationName string = ''

@description('Claude Marketplace attestation: ISO 3166-1 alpha-2 country code.')
param claudeCountryCode string = 'US'

@allowed([
  'technology'
  'finance'
  'healthcare'
  'education'
  'retail'
  'manufacturing'
  'government'
  'media'
  'other'
])
@description('Claude Marketplace attestation: industry.')
param claudeIndustry string = 'technology'

@description('Optional tags applied to every resource.')
param tags object = {}

var resourceToken = toLower(uniqueString(subscription().id, environmentName, location))
var allTags = union(tags, { 'azd-env-name': environmentName })
var projectName = 'media-studio'

resource rg 'Microsoft.Resources/resourceGroups@2024-03-01' = {
  name: 'rg-${environmentName}'
  location: location
  tags: allTags
}

module foundry 'modules/foundry.bicep' = {
  name: 'foundry'
  scope: rg
  params: {
    name: 'ai-${resourceToken}'
    location: location
    tags: allTags
    projectName: projectName
    principalId: principalId
    principalType: principalType
    assignRbac: assignRbac
    disableLocalAuth: disableLocalAuth
    flags: {
      deployOpenAIAudio: deployOpenAIAudio
      deployMistralOcr: deployMistralOcr
      deployClaude: deployClaude
    }
    embeddingModel: cuEmbeddingModel
    claudeOrganizationName: claudeOrganizationName
    claudeCountryCode: claudeCountryCode
    claudeIndustry: claudeIndustry
  }
}

output AZURE_LOCATION string = location
output AZURE_RESOURCE_GROUP string = rg.name
output AZURE_FOUNDRY_NAME string = foundry.outputs.name
output AZURE_FOUNDRY_RESOURCE_ID string = foundry.outputs.id
output AZURE_FOUNDRY_ENDPOINT string = foundry.outputs.endpoint
output AZURE_FOUNDRY_PROJECT_ENDPOINT string = foundry.outputs.projectEndpoint
output MEDIASTUDIO_DEPLOYMENTS string = string(foundry.outputs.deployments)
output MEDIASTUDIO_CU_ENABLED bool = enableContentUnderstanding
output MEDIASTUDIO_CU_EMBEDDING_MODEL string = cuEmbeddingModel
output MEDIASTUDIO_CLAUDE_ENABLED bool = deployClaude
output MEDIASTUDIO_OPENAI_AUDIO_ENABLED bool = deployOpenAIAudio
output MEDIASTUDIO_MISTRAL_OCR_ENABLED bool = deployMistralOcr
