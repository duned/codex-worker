import type { server as source } from '../en/server';
export const server = {
  "server.serverGitHubConnection": "Conexión GitHub del servidor",
  "server.serverAuthenticationDoesNotEstablishRepositoryWritePermissionOrWorkerReadiness": "La autenticación del servidor no demuestra permiso de escritura en el repositorio ni preparación del agente.",
  "server.manageServerGitHubConnection": "Administrar conexión GitHub del servidor",
  "server.observations": "Observaciones:",
  "server.provisioning": ". Aprovisionamiento:",
  "server.disabledLocally": "deshabilitado localmente",
  "server.authenticationCheckFailedInspectConnection": "Comprobación de autenticación fallida · inspeccionar conexión",
  "server.currentServerGitHubAuthentication": "Autenticación GitHub actual del servidor",
  "server.connectionUnavailable": "Observaciones de conexión no disponibles",
  "server.observationsStale": "Observaciones obsoletas · comprobar autenticación",
  "server.authenticationState": "Autenticación: {state}"
} satisfies Record<keyof typeof source, string>;
