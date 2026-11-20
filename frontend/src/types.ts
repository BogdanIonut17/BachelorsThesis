export type SessionStatus =
  | 'Uploaded'
  | 'Queued'
  | 'Processing'
  | 'RequiresManualReference'
  | 'ReadyForReview'
  | 'Failed';

export interface SensitiveMatch {
  lineNumber: number;
  ruleName: string;
  maskedPreview: string;
}

export interface VideoMetadata {
  durationSeconds: number;
  width: number;
  height: number;
  frameRate: number;
  hasAudio: boolean;
}

export interface ExtractedFrame {
  index: number;
  timestampSeconds: number;
  fileName: string;
}

export interface AudioActivityInterval {
  startSeconds: number;
  endSeconds: number;
  isActive: boolean;
}

export type TechnicalEventCategory = 'Command' | 'Build' | 'Container' | 'Error' | 'ExitCode' | 'Output';

export interface TechnicalEvent {
  index: number;
  timestampSeconds: number;
  eventType: string;
  category: TechnicalEventCategory;
  text: string;
  isAnchorable: boolean;
}

export interface SessionArtifacts {
  video: VideoMetadata;
  frames: ExtractedFrame[];
  audioIntervals: AudioActivityInterval[];
  technicalEvents: TechnicalEvent[];
}

export interface SessionArtifactsDto extends SessionArtifacts {
  appliedReferenceOffsetSeconds: number | null;
}

export interface ProcessingSessionDto {
  id: string;
  videoFileName: string;
  logFileName: string;
  status: SessionStatus;
  progressPercent: number;
  manualReferenceOffsetSeconds: number | null;
  isAutoSyncFeasible: boolean;
  syncWarningMessage: string | null;
  parsedEventCount: number;
  detectedSecrets: SensitiveMatch[];
  createdAt: string;
  processingStage?: string | null;
  errorMessage?: string | null;
  artifacts?: SessionArtifacts | null;
}

export const TERMINAL_STATUSES: readonly SessionStatus[] = [
  'RequiresManualReference',
  'ReadyForReview',
  'Failed',
];

export const isTerminalStatus = (status: SessionStatus): boolean =>
  TERMINAL_STATUSES.includes(status);
