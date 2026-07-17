// Tauri IPC event schemas for streaming Run updates

export type RunEventType =
  | 'run:started'
  | 'run:token'
  | 'run:tool-call'
  | 'run:tool-result'
  | 'run:message'
  | 'run:usage'
  | 'run:phase'
  | 'run:plan'
  | 'run:verify'
  | 'run:approval-requested'
  | 'run:approval-resolved'
  | 'run:changeset'
  | 'run:completed'
  | 'run:failed'
  | 'run:cancelled';

export interface RunEvent {
  type: RunEventType;
  runId: string;
  timestamp: string;
}

export interface RunStartedEvent extends RunEvent {
  type: 'run:started';
}

export interface RunTokenEvent extends RunEvent {
  type: 'run:token';
  token: string;
}

export interface RunToolCallEvent extends RunEvent {
  type: 'run:tool-call';
  toolName: string;
  toolInput: Record<string, unknown>;
}

export interface RunToolResultEvent extends RunEvent {
  type: 'run:tool-result';
  toolName: string;
  result: unknown;
}

export interface RunCompletedEvent extends RunEvent {
  type: 'run:completed';
  output: string;
}

export interface RunFailedEvent extends RunEvent {
  type: 'run:failed';
  error: string;
}

export interface RunApprovalRequestedEvent extends RunEvent {
  type: 'run:approval-requested';
  approvalId: string;
  operation: string;
  target?: string;
  summary?: string;
  capabilities: string;
  riskLevel: 'low' | 'medium' | 'high' | 'critical';
  createdAt: string;
}

export interface RunApprovalResolvedEvent extends RunEvent {
  type: 'run:approval-resolved';
  approvalId: string;
  status: 'approved' | 'rejected' | 'cancelled' | 'expired';
  scope?: 'once' | 'run' | 'workspace';
  decisionComment?: string;
  resolvedAt?: string;
}

export interface RunChangeSetEvent extends RunEvent {
  type: 'run:changeset';
  checkpointId: string;
  fileCount: number;
  files: Array<{
    relativePath: string;
    kind: 'added' | 'modified' | 'deleted' | 'renamed';
    binary: boolean;
    unifiedDiff?: string;
  }>;
}

export type AnyRunEvent =
  | RunStartedEvent
  | RunTokenEvent
  | RunToolCallEvent
  | RunToolResultEvent
  | RunApprovalRequestedEvent
  | RunApprovalResolvedEvent
  | RunChangeSetEvent
  | RunCompletedEvent
  | RunFailedEvent;
