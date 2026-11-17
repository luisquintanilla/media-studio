// Data-plane roles on the Foundry account for the developer (User) or CI identity (ServicePrincipal).
// GUIDs from https://learn.microsoft.com/azure/role-based-access-control/built-in-roles/ai-machine-learning (2026-11-16).
param accountName string
param principalId string
param principalType string

// Cognitive Services User: data actions Microsoft.CognitiveServices/* -> OpenAI v1 (chat, responses, audio,
// images, decisions), Speech (MAI-Voice / MAI-Transcribe), Document Intelligence, Content Understanding, Mistral.
var cognitiveServicesUser = 'a97b65f3-24c7-4388-baec-2e87135dc908'
// Foundry User (formerly "Azure AI User"): Foundry project data plane (agents, project endpoint, Anthropic route).
var foundryUser = '53ca6127-db72-4b80-b1b0-d745d6d5456d'

resource account 'Microsoft.CognitiveServices/accounts@2025-10-01-preview' existing = {
  name: accountName
}

resource roleAssignments 'Microsoft.Authorization/roleAssignments@2022-04-01' = [
  for roleId in [cognitiveServicesUser, foundryUser]: {
    name: guid(account.id, principalId, roleId)
    scope: account
    properties: {
      principalId: principalId
      principalType: principalType
      roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roleId)
    }
  }
]
