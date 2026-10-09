// Narrow projections of existing Server read contracts. Unused fields are deliberately omitted.
export interface WorkerObservation {
  authenticationCredentialStatus?: string; capabilities?: { type: string; name: string; scope?: string; version?: string }[]; workerVersion?: string;
  workerId: string; displayName?: string; availability: string; lifecycleState?: string;
  capacity?: number; maximumCapacity?: number; activeExecutions?: number; availableCapacity?: number;
  activeAssignments?: number; schedulingPolicy?: string; lastHeartbeatAtUtc?: string;
  hostResources?: { measuredAtUtc: string; logicalCpuCount?: number; totalMemoryBytes?: number; usedMemoryBytes?: number; diskTotalBytes?: number; diskAvailableBytes?: number; cpuUsagePercent?: number; memoryUsagePercent?: number; sampleSeconds?: number };
  platform?: string; firstRegisteredAtUtc?: string; activeProjects?: string[];
}
export interface ProjectSummary { id: string; name: string; repository: string; revision?: number; enabled?: boolean; requirements?: { type: string; name: string; version?: string }[] }
export interface ExecutionSummary {
  id: string; projectId: string; assignedWorkerId?: string; state: string; createdAtUtc: string;
  currentStage?: string; startedAtUtc?: string; assignedAtUtc?: string; completedAtUtc?: string;
  durationMilliseconds?: number; recoveryState?: string; recoveryReason?: string; pendingReason?: string;
  managedEligibilityState?: string; managedEligibilityReasons?: string[]; managedEligibilityCheckedAtUtc?: string;
  completionSummary?: string;
  workReference?: { type: string; id: string; url?: string };
}
export interface ExecutionMaintenanceRequest {
  operationId: string; workerId: string; serverExecutionId?: string | null; workerExecutionId?: string | null;
  assignmentId?: string | null; generation?: number | null; action: 'inventory' | 'inspect' | 'cleanup' | 'archive' | 'retry-report';
  apply: boolean; timeoutSeconds: number; limit: number; offset: number;
}
export interface ExecutionMaintenanceObservation {
  executionId: string; serverExecutionId?: string; assignmentId?: string; generation?: number;
  state: string; recoveryState: string; reportingStatus: string; project: string; issueNumber: number; archived: boolean;
}
export interface ExecutionMaintenanceReport { outcome: string; reason: string; observations: ExecutionMaintenanceObservation[] }
export interface ExecutionMaintenanceCommand {
  request: ExecutionMaintenanceRequest; status: string; createdAtUtc: string; authorizedBy: string;
  deadlineUtc?: string; report?: ExecutionMaintenanceReport; completedAtUtc?: string;
}
export interface ExecutionMaintenanceDetail {
  operation: ExecutionMaintenanceCommand; execution?: ExecutionSummary | null; workerStatus: string;
  observations: { observation: ExecutionMaintenanceObservation; execution?: ExecutionSummary | null; status: string }[];
  status: string;
}
export interface Capability {
  definition: { id: string; displayName: string; requiresAuthentication: boolean; requiresConfiguration: boolean };
  state: { installation: string; health: string; authentication?: string; configuration?: string; update?: string;
    detectedVersion?: string; detectedAtUtc?: string; diagnosticCode?: string; operation?: { state: string; action?: string; diagnosticCode?: string } };
  availableActions: string[];
}
export interface NodeSummary {
  id: string; kind: string; displayName?: string; health?: string; connectivity: string; executionReadiness: string;
  provisioningReadiness?: string; observationsStale: boolean; capabilities: Capability[];
}
export interface WorkerDiagnostics {
  aiAgentReady: boolean; gitHubReady: boolean; gitReady: boolean;
  configurationSynchronization: string; provisioningState: string; workerVersion?: string; capabilityObservationsCurrent?: boolean; projects?: WorkerProjectReadiness[]; reasons?: string[];
  recentOperationalError?: string;
}
export interface WorkerAdministration extends WorkerObservation {
  authenticationCredentialRevokedAtUtc?: string;
}
export interface WorkerReadiness extends WorkerDiagnostics {
  canActivate?: boolean; activationBlockingReasons?: string[];
}
export interface NodeCommandSummary {
  id: string; createdAtUtc: string; status: string; diagnostic?: string;
  startedAtUtc?: string; deadlineUtc?: string; completedAtUtc?: string;
  publicIdentity?: { publicKey: string; fingerprint: string };
  failureDetail?: { description: string };
  loginInstructions?: { verificationUri: string; userCode: string };
  request: { nodeId: string; capabilityId: string; action: string };
}

export interface ServerGitHubConnection { commands: NodeCommandSummary[]; provisioningEnabled: boolean; elevationAllowed: boolean }
export interface ServerStatus { state: string; version: string; startedAtUtc: string }

export interface WorkerProjectReadiness {
  projectId: string; projectName: string; isEligible: boolean; missingRequirements: string[];
  workerReportedRevision?: number; materializationState?: string; diagnosticCode?: string; observationStatus: string;
}
export interface DeliveryAuthorization { status: string; revokedAtUtc?: string }
export interface PairingRequest { contractVersion: 1; workerId: string; operation: 'enroll' | 'associate'; server: string }
export interface PairingAuthorization { authorization: string; lifetimeSeconds: number }
export interface ProvisioningPlanSummary {
  id: string; workerId: string; createdAtUtc: string; state: string; currentActionId?: string;
  actions: { type: string; name: string; version?: string }[];
}
