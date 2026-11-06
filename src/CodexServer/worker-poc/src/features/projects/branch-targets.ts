import type { Project } from './contracts';
import { readBranchTargets } from './archived-branches.mjs';

export interface BranchTarget {
  workerId: string; displayName: string; mode: 'managed' | 'standalone-observer';
  eligible: boolean; reason: string | null; canApply: boolean; applyReason: string | null;
}
export interface BranchTargets { projectId: string; revision: number; project: string; repository: string; targets: BranchTarget[] }
export const branchTargetsFor = (project: Project) => (value: unknown): BranchTargets => readBranchTargets(value, project);
