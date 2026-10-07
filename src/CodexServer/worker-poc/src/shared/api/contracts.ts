// Narrow projections of existing Server read contracts. Unused fields are deliberately omitted.
export interface WorkerObservation {
  workerId: string; displayName?: string; availability: string; lifecycleState?: string;
  capacity?: number; maximumCapacity?: number; activeExecutions?: number; availableCapacity?: number;
  activeAssignments?: number; schedulingPolicy?: string; lastHeartbeatAtUtc?: string;
}
export interface ProjectSummary { id: string; name: string; repository: string }
export interface ExecutionSummary {
  id: string; projectId: string; assignedWorkerId?: string; state: string; createdAtUtc: string;
  currentStage?: string; startedAtUtc?: string; assignedAtUtc?: string; completedAtUtc?: string;
  durationMilliseconds?: number; recoveryState?: string;
  workReference?: { type: string; id: string; url?: string };
}
export interface Capability {
  definition: { id: string; displayName: string; requiresAuthentication: boolean; requiresConfiguration: boolean };
  state: { installation: string; health: string; authentication?: string; configuration?: string; update?: string;
    detectedVersion?: string; detectedAtUtc?: string; diagnosticCode?: string; operation?: { state: string; action?: string; diagnosticCode?: string } };
  availableActions: string[];
}
export interface NodeSummary {
  id: string; kind: string; connectivity: string; executionReadiness: string;
  provisioningReadiness?: string; observationsStale: boolean; capabilities: Capability[];
}
export interface WorkerDiagnostics {
  aiAgentReady: boolean; gitHubReady: boolean; gitReady: boolean;
  configurationSynchronization: string; provisioningState: string;
}
export interface WorkerAdministration extends WorkerObservation {
  authenticationCredentialStatus?: string; authenticationCredentialRevokedAtUtc?: string;
}
export interface WorkerReadiness extends WorkerDiagnostics {
  canActivate?: boolean; activationBlockingReasons?: string[];
}
export interface NodeCommandSummary {
  id: string; createdAtUtc: string; status: string;
  request: { nodeId: string; capabilityId: string; action: string };
}
