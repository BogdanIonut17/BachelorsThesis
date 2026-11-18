import { useEffect, useState } from 'react';
import type { FormEvent } from 'react';
import * as defaultApi from '../api';
import type { UploadInput } from '../api';
import type { ProcessingSessionDto } from '../types';
import { isTerminalStatus } from '../types';
import { LOG_EXTENSIONS, VIDEO_EXTENSIONS, parseOffset, validateLogFile, validateVideoFile } from '../validation';
import { FileDropzone } from './FileDropzone';
import { PrivacyAlert } from './PrivacyAlert';
import { ProgressBar } from './ProgressBar';

export interface WorkspaceApi {
  uploadSession: (input: UploadInput, onProgress: (percent: number) => void) => Promise<ProcessingSessionDto>;
  fetchStatus: (id: string, signal?: AbortSignal) => Promise<ProcessingSessionDto>;
  setReferencePoint: (id: string, offsetSeconds: number) => Promise<ProcessingSessionDto>;
}

interface UploadWorkspaceProps {
  api?: WorkspaceApi;
  pollIntervalMs?: number;
}

type Phase = 'idle' | 'uploading' | 'tracking';

const messageOf = (error: unknown): string => (error instanceof Error ? error.message : 'Unexpected error.');

export default function UploadWorkspace({ api = defaultApi, pollIntervalMs = 1500 }: UploadWorkspaceProps) {
  const [videoFile, setVideoFile] = useState<File | null>(null);
  const [logFile, setLogFile] = useState<File | null>(null);
  const [videoError, setVideoError] = useState<string | null>(null);
  const [logError, setLogError] = useState<string | null>(null);
  const [offsetText, setOffsetText] = useState('');

  const [phase, setPhase] = useState<Phase>('idle');
  const [uploadPercent, setUploadPercent] = useState(0);
  const [session, setSession] = useState<ProcessingSessionDto | null>(null);
  const [requestError, setRequestError] = useState<string | null>(null);
  const [pollRetry, setPollRetry] = useState(0);

  const [calibrationText, setCalibrationText] = useState('');
  const [calibrationError, setCalibrationError] = useState<string | null>(null);
  const [isCalibrating, setIsCalibrating] = useState(false);

  const parsedOffset = parseOffset(offsetText);
  const offsetError = parsedOffset === undefined ? 'Manual reference point must be a number of seconds.' : null;
  const isBusy = phase !== 'idle';
  const canSubmit = videoFile !== null && logFile !== null && offsetError === null && !isBusy;

  useEffect(() => {
    if (!session || isTerminalStatus(session.status)) {
      return;
    }

    const controller = new AbortController();
    const timer = setTimeout(async () => {
      try {
        const next = await api.fetchStatus(session.id, controller.signal);
        setRequestError(null);
        setSession(next);
      } catch (error) {
        if (controller.signal.aborted) {
          return;
        }
        setRequestError(messageOf(error));
        setPollRetry((count) => count + 1);
      }
    }, pollIntervalMs);

    return () => {
      clearTimeout(timer);
      controller.abort();
    };
  }, [api, session, pollIntervalMs, pollRetry]);

  const selectVideo = (file: File) => {
    const error = validateVideoFile(file);
    setVideoError(error);
    setVideoFile(error ? null : file);
  };

  const selectLog = (file: File) => {
    const error = validateLogFile(file);
    setLogError(error);
    setLogFile(error ? null : file);
  };

  const handleSubmit = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    if (!canSubmit || !videoFile || !logFile) {
      return;
    }

    setPhase('uploading');
    setUploadPercent(0);
    setRequestError(null);
    setSession(null);

    try {
      const created = await api.uploadSession(
        {
          videoFile,
          logFile,
          manualReferenceOffsetSeconds: parsedOffset ?? null,
        },
        setUploadPercent,
      );
      setSession(created);
      setPhase('tracking');
    } catch (error) {
      setRequestError(messageOf(error));
      setPhase('idle');
    }
  };

  const handleCalibrate = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    if (!session) {
      return;
    }

    const value = parseOffset(calibrationText);
    if (value === undefined || value === null) {
      setCalibrationError('Enter the reference offset in seconds.');
      return;
    }

    setCalibrationError(null);
    setIsCalibrating(true);
    try {
      setSession(await api.setReferencePoint(session.id, value));
      setCalibrationText('');
    } catch (error) {
      setCalibrationError(messageOf(error));
    } finally {
      setIsCalibrating(false);
    }
  };

  const reset = () => {
    setVideoFile(null);
    setLogFile(null);
    setVideoError(null);
    setLogError(null);
    setOffsetText('');
    setSession(null);
    setRequestError(null);
    setUploadPercent(0);
    setPhase('idle');
  };

  const status = session?.status;
  const inFlight = session !== null && !isTerminalStatus(session.status);

  return (
    <div className="mx-auto flex max-w-3xl flex-col gap-8 p-6">
      <header>
        <h1 className="text-2xl font-semibold text-slate-900">DemoSync Workspace</h1>
        <p className="mt-1 text-sm text-slate-600">
          Upload a screen recording and its execution log. The log is scanned for secrets and checked for
          synchronization feasibility before processing starts.
        </p>
      </header>

      <form onSubmit={handleSubmit} noValidate className="flex flex-col gap-6" aria-label="Upload form">
        <FileDropzone
          testId="video-dropzone"
          label="Screen Recording"
          hint={`Accepted: ${VIDEO_EXTENSIONS.join(', ')} (max 500 MB)`}
          accept={VIDEO_EXTENSIONS.join(',')}
          file={videoFile}
          error={videoError}
          disabled={isBusy}
          onFileSelected={selectVideo}
        />
        <FileDropzone
          testId="log-dropzone"
          label="Execution Log"
          hint={`Accepted: ${LOG_EXTENSIONS.join(', ')} (max 25 MB)`}
          accept={LOG_EXTENSIONS.join(',')}
          file={logFile}
          error={logError}
          disabled={isBusy}
          onFileSelected={selectLog}
        />

        <div className="flex flex-col gap-1">
          <label htmlFor="manual-offset" className="text-sm font-medium text-slate-700">
            Manual Reference Point (seconds)
          </label>
          <input
            id="manual-offset"
            type="number"
            step="any"
            inputMode="decimal"
            value={offsetText}
            disabled={isBusy}
            onChange={(event) => setOffsetText(event.target.value)}
            placeholder="Optional"
            aria-invalid={offsetError ? true : undefined}
            className="w-48 rounded-md border border-slate-300 px-3 py-2 text-sm shadow-sm focus:border-indigo-500 focus:outline-none disabled:opacity-50"
          />
          {offsetError && (
            <p role="alert" className="text-sm text-red-600">
              {offsetError}
            </p>
          )}
        </div>

        <div>
          <button
            type="submit"
            disabled={!canSubmit}
            className="rounded-md bg-indigo-600 px-5 py-2 text-sm font-semibold text-white shadow-sm hover:bg-indigo-500 disabled:cursor-not-allowed disabled:bg-slate-300"
          >
            {phase === 'uploading' ? 'Uploading…' : 'Submit'}
          </button>
        </div>
      </form>

      {requestError && (
        <p role="alert" className="rounded-md border border-red-300 bg-red-50 p-3 text-sm text-red-700">
          {requestError}
        </p>
      )}

      {(phase === 'uploading' || session) && (
        <section aria-label="Upload progress" className="flex flex-col gap-4 rounded-lg border border-slate-200 bg-white p-4">
          <ProgressBar label="Upload progress" percent={phase === 'uploading' ? uploadPercent : 100} />

          {session && (
            <>
              <ProgressBar
                label="Processing progress"
                percent={session.progressPercent}
                tone={status === 'ReadyForReview' ? 'emerald' : 'indigo'}
              />
              <dl className="grid grid-cols-2 gap-2 text-sm">
                <dt className="text-slate-500">Status</dt>
                <dd data-testid="session-status" className="font-medium text-slate-900">
                  {session.status}
                </dd>
                <dt className="text-slate-500">Parsed events</dt>
                <dd data-testid="parsed-event-count" className="font-medium text-slate-900">
                  {session.parsedEventCount}
                </dd>
                {session.manualReferenceOffsetSeconds !== null && (
                  <>
                    <dt className="text-slate-500">Manual reference offset</dt>
                    <dd className="font-medium text-slate-900">{session.manualReferenceOffsetSeconds}s</dd>
                  </>
                )}
              </dl>
              {inFlight && <p className="text-sm text-slate-500">Waiting for the processing job…</p>}
              {status === 'ReadyForReview' && (
                <p role="status" className="text-sm font-medium text-emerald-700">
                  Processing complete. The session is ready for review.
                </p>
              )}
              {status === 'Failed' && (
                <p role="alert" className="text-sm font-medium text-red-700">
                  Processing failed. Please try uploading again.
                </p>
              )}
            </>
          )}
        </section>
      )}

      {session && status === 'RequiresManualReference' && (
        <section
          aria-labelledby="calibration-title"
          className="rounded-lg border border-sky-300 bg-sky-50 p-4"
        >
          <h2 id="calibration-title" className="text-base font-semibold text-sky-900">
            Manual offset calibration required
          </h2>
          <p className="mt-1 text-sm text-sky-800">
            {session.syncWarningMessage ?? 'Automatic synchronization is not possible for this log.'}
          </p>
          <form onSubmit={handleCalibrate} noValidate className="mt-3 flex flex-wrap items-end gap-3">
            <div className="flex flex-col gap-1">
              <label htmlFor="calibration-offset" className="text-sm font-medium text-sky-900">
                Reference offset (seconds)
              </label>
              <input
                id="calibration-offset"
                type="number"
                step="any"
                inputMode="decimal"
                value={calibrationText}
                onChange={(event) => setCalibrationText(event.target.value)}
                className="w-48 rounded-md border border-sky-300 px-3 py-2 text-sm focus:border-sky-500 focus:outline-none"
              />
            </div>
            <button
              type="submit"
              disabled={isCalibrating || calibrationText.trim() === ''}
              className="rounded-md bg-sky-700 px-4 py-2 text-sm font-semibold text-white hover:bg-sky-600 disabled:cursor-not-allowed disabled:bg-slate-300"
            >
              Apply reference point
            </button>
          </form>
          {calibrationError && (
            <p role="alert" className="mt-2 text-sm text-red-600">
              {calibrationError}
            </p>
          )}
        </section>
      )}

      {session && <PrivacyAlert secrets={session.detectedSecrets} />}

      {session && (status === 'ReadyForReview' || status === 'Failed') && (
        <div>
          <button
            type="button"
            onClick={reset}
            className="rounded-md border border-slate-300 bg-white px-4 py-2 text-sm font-medium text-slate-700 hover:bg-slate-50"
          >
            Start new upload
          </button>
        </div>
      )}
    </div>
  );
}
