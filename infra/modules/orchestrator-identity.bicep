// Identity the self-hosted orchestrator runs as.
//
// Created in every deployment, including the ones where the Foundry Agent Service hosts the
// orchestrator and nothing uses it. A system-assigned identity does not exist until its compute
// does, and the Foundry role assignment that identity needs is declared in the same deployment that
// would create that compute, so a system-assigned one forces two passes. A user-assigned identity
// exists before either, and costs nothing when it goes unused.

param name string
param location string
param tags object = {}

resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: name
  location: location
  tags: tags
}

output id string = identity.id
output clientId string = identity.properties.clientId
output principalId string = identity.properties.principalId
