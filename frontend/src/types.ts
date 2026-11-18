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
}

export const TERMINAL_STATUSES: readonly SessionStatus[] = [
  'RequiresManualReference',
  'ReadyForReview',
  'Failed',
];

export const isTerminalStatus = (status: SessionStatus): boolean =>
  TERMINAL_STATUSES.includes(status);
