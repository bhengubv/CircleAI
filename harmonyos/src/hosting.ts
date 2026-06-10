// hosting.ts — IAIObserver + AIOptions for the hosting layer.

import { ChatResponse, UpgradeInfo } from './models_v15';
import { ChatCapability } from './selector';

export interface IAIObserver {
  onStarted?(): Promise<void>;
  onStopped?(): Promise<void>;
  onChatCompleted?(response: ChatResponse): Promise<void>;
  onStreamStarted?(modelId: string): Promise<void>;
  onStreamCompleted?(modelId: string, tokenCount: number): Promise<void>;
  onToolInvoked?(toolName: string, success: boolean): Promise<void>;
  onModelFetching?(modelId: string, autoSelected: boolean): Promise<void>;
  onUpgradeAvailable?(upgrade: UpgradeInfo): Promise<void>;
}

export interface AIOptions {
  readonly modelId: string | null;
  readonly modelPath: string | null;
  readonly systemPrompt: string;
  readonly contextSize: number | null;
  readonly threadCount: number | null;
  readonly warmOnStart: boolean;
  readonly requiredCapabilities: number;
  readonly agenticMaxIterations: number | null;
  readonly observer: IAIObserver | null;
  readonly checkForUpgradesOnStart: boolean;
  readonly modelStorageDirectory: string | null;
}

export function defaultAIOptions(): AIOptions {
  return {
    modelId: null,
    modelPath: null,
    systemPrompt: 'You are B!, a helpful on-device assistant.',
    contextSize: null,
    threadCount: null,
    warmOnStart: true,
    requiredCapabilities: ChatCapability.Default,
    agenticMaxIterations: null,
    observer: null,
    checkForUpgradesOnStart: false,
    modelStorageDirectory: null,
  };
}
