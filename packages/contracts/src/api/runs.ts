// Tauri IPC payload types for Run operations

export type AgentMode = 'ask' | 'plan' | 'auto' | 'full_auto';
export type WorkspaceStrategy = 'direct' | 'git_worktree' | 'svn_shadow_git' | 'snapshot';
export type RunStatus =
  | 'idle'
  | 'created'
  | 'running'
  | 'waiting_approval'
  | 'paused'
  | 'completed'
  | 'failed'
  | 'cancelled';

export interface CreateRunRequest {
  workflowId: string;
  input: Record<string, unknown>;
  options?: RunOptions;
}

export interface RunOptions {
  modelProvider?: 'openai' | 'anthropic' | 'azure-openai' | 'openrouter' | 'custom';
  modelId?: string;
  temperature?: number;
  maxTokens?: number;
  agentMode?: AgentMode;
  workspaceStrategy?: WorkspaceStrategy;
  workspacePath?: string;
  projectId?: string;
}

export interface RunResponse {
  runId: string;
  status: RunStatus;
  createdAt: string;
}

export interface RunState {
  runId: string;
  status: RunStatus;
  output?: string;
  error?: string;
  createdAt: string;
  updatedAt: string;
}
