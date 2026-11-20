import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import UploadWorkspace from '../components/UploadWorkspace';
import type { WorkspaceApi } from '../components/UploadWorkspace';
import type { ProcessingSessionDto } from '../types';

const baseSession: ProcessingSessionDto = {
  id: '11111111-1111-1111-1111-111111111111',
  videoFileName: 'demo.mp4',
  logFileName: 'demo.cast',
  status: 'Queued',
  progressPercent: 0,
  manualReferenceOffsetSeconds: null,
  isAutoSyncFeasible: true,
  syncWarningMessage: null,
  parsedEventCount: 42,
  detectedSecrets: [],
  createdAt: '2026-11-18T20:00:00Z',
};

const makeApi = (overrides: Partial<WorkspaceApi> = {}): WorkspaceApi => ({
  uploadSession: vi.fn().mockResolvedValue(baseSession),
  fetchStatus: vi.fn().mockResolvedValue({ ...baseSession, status: 'ReadyForReview', progressPercent: 100 }),
  setReferencePoint: vi.fn().mockResolvedValue({ ...baseSession, status: 'Queued' }),
  fetchArtifacts: vi.fn().mockResolvedValue({
    video: { durationSeconds: 10, width: 1280, height: 720, frameRate: 30, hasAudio: true },
    frames: [],
    audioIntervals: [],
    technicalEvents: [],
    appliedReferenceOffsetSeconds: null,
  }),
  ...overrides,
});

const videoInput = () => screen.getByLabelText(/screen recording/i, { selector: 'input' });
const logInput = () => screen.getByLabelText(/execution log/i, { selector: 'input' });
const submitButton = () => screen.getByRole('button', { name: /^submit$/i });

const mp4 = () => new File(['video-bytes'], 'demo.mp4', { type: 'video/mp4' });
const cast = () => new File(['{"version":2}'], 'demo.cast', { type: 'application/octet-stream' });

describe('UploadWorkspace', () => {
  it('keeps Submit disabled until both video and log files are selected', async () => {
    const user = userEvent.setup({ applyAccept: false });
    render(<UploadWorkspace api={makeApi()} pollIntervalMs={5} />);

    expect(submitButton()).toBeDisabled();

    await user.upload(videoInput(), mp4());
    expect(submitButton()).toBeDisabled();

    await user.upload(logInput(), cast());
    expect(submitButton()).toBeEnabled();
  });

  it('keeps Submit disabled when only the log file is provided', async () => {
    const user = userEvent.setup({ applyAccept: false });
    render(<UploadWorkspace api={makeApi()} pollIntervalMs={5} />);

    await user.upload(logInput(), cast());

    expect(submitButton()).toBeDisabled();
  });

  it('shows a validation error and rejects files with unsupported extensions', async () => {
    const user = userEvent.setup({ applyAccept: false });
    render(<UploadWorkspace api={makeApi()} pollIntervalMs={5} />);

    await user.upload(videoInput(), new File(['x'], 'demo.avi', { type: 'video/x-msvideo' }));
    await user.upload(logInput(), new File(['x'], 'notes.txt', { type: 'text/plain' }));

    expect(screen.getByText(/screen recording must be one of: \.mp4, \.mkv, \.webm/i)).toBeInTheDocument();
    expect(screen.getByText(/execution log must be one of: \.cast, \.jsonl, \.log/i)).toBeInTheDocument();
    expect(submitButton()).toBeDisabled();
  });

  it('shows a validation error for empty files', async () => {
    const user = userEvent.setup({ applyAccept: false });
    render(<UploadWorkspace api={makeApi()} pollIntervalMs={5} />);

    await user.upload(logInput(), new File([], 'empty.log', { type: 'text/plain' }));

    expect(screen.getByText(/execution log must not be empty/i)).toBeInTheDocument();
    expect(submitButton()).toBeDisabled();
  });

  it('accepts files dropped onto the dropzone', () => {
    render(<UploadWorkspace api={makeApi()} pollIntervalMs={5} />);

    const dropzone = screen.getByTestId('video-dropzone');
    fireEvent.drop(dropzone, { dataTransfer: { files: [mp4()] } });

    expect(screen.getByText(/demo\.mp4/)).toBeInTheDocument();
  });

  it('keeps Submit enabled when an optional manual reference point is entered', async () => {
    const user = userEvent.setup({ applyAccept: false });
    render(<UploadWorkspace api={makeApi()} pollIntervalMs={5} />);

    await user.upload(videoInput(), mp4());
    await user.upload(logInput(), cast());
    await user.type(screen.getByLabelText(/manual reference point/i), '12.5');

    expect(submitButton()).toBeEnabled();
  });

  it('uploads, polls until a terminal state, and renders progress details', async () => {
    const user = userEvent.setup({ applyAccept: false });
    const api = makeApi({
      fetchStatus: vi
        .fn()
        .mockResolvedValueOnce({ ...baseSession, status: 'Processing', progressPercent: 60 })
        .mockResolvedValue({ ...baseSession, status: 'ReadyForReview', progressPercent: 100 }),
    });
    render(<UploadWorkspace api={api} pollIntervalMs={5} />);

    await user.upload(videoInput(), mp4());
    await user.upload(logInput(), cast());
    await user.type(screen.getByLabelText(/manual reference point/i), '3.5');
    await user.click(submitButton());

    await waitFor(() => expect(screen.getByTestId('session-status')).toHaveTextContent('ReadyForReview'));

    expect(api.uploadSession).toHaveBeenCalledWith(
      expect.objectContaining({ manualReferenceOffsetSeconds: 3.5 }),
      expect.any(Function),
    );
    expect(screen.getByTestId('parsed-event-count')).toHaveTextContent('42');
    expect(screen.getByRole('progressbar', { name: /processing progress/i })).toHaveAttribute('aria-valuenow', '100');
    expect(screen.queryByRole('heading', { name: /privacy alert/i })).not.toBeInTheDocument();
  });

  it('renders the privacy alert table when secrets are detected', async () => {
    const user = userEvent.setup({ applyAccept: false });
    const api = makeApi({
      uploadSession: vi.fn().mockResolvedValue({
        ...baseSession,
        status: 'ReadyForReview',
        progressPercent: 100,
        detectedSecrets: [
          { lineNumber: 7, ruleName: 'OpenAiApiKey', maskedPreview: 'sk-a************' },
          { lineNumber: 12, ruleName: 'Password', maskedPreview: 'pass************' },
        ],
      }),
    });
    render(<UploadWorkspace api={api} pollIntervalMs={5} />);

    await user.upload(videoInput(), mp4());
    await user.upload(logInput(), cast());
    await user.click(submitButton());

    const alert = await screen.findByRole('alert', { name: /privacy alert/i });
    expect(within(alert).getByText('7')).toBeInTheDocument();
    expect(within(alert).getByText('OpenAiApiKey')).toBeInTheDocument();
    expect(within(alert).getByText('sk-a************')).toBeInTheDocument();
    expect(within(alert).getByText('Password')).toBeInTheDocument();
    expect(within(alert).getAllByRole('row')).toHaveLength(3);
  });

  it('prompts for manual calibration and resubmits the offset', async () => {
    const user = userEvent.setup({ applyAccept: false });
    const api = makeApi({
      uploadSession: vi.fn().mockResolvedValue({
        ...baseSession,
        status: 'RequiresManualReference',
        isAutoSyncFeasible: false,
        syncWarningMessage: 'Timestamps are missing or corrupted.',
      }),
    });
    render(<UploadWorkspace api={api} pollIntervalMs={5} />);

    await user.upload(videoInput(), mp4());
    await user.upload(logInput(), cast());
    await user.click(submitButton());

    expect(await screen.findByText(/manual offset calibration required/i)).toBeInTheDocument();
    expect(screen.getByText(/timestamps are missing or corrupted/i)).toBeInTheDocument();

    await user.type(screen.getByLabelText(/reference offset \(seconds\)/i), '4.25');
    await user.click(screen.getByRole('button', { name: /apply reference point/i }));

    await waitFor(() => expect(api.setReferencePoint).toHaveBeenCalledWith(baseSession.id, 4.25));
    await waitFor(() => expect(screen.queryByText(/manual offset calibration required/i)).not.toBeInTheDocument());
  });

  it('shows the server error when the upload is rejected', async () => {
    const user = userEvent.setup({ applyAccept: false });
    const api = makeApi({
      uploadSession: vi.fn().mockRejectedValue(new Error('Execution log must be valid UTF-8 text.')),
    });
    render(<UploadWorkspace api={api} pollIntervalMs={5} />);

    await user.upload(videoInput(), mp4());
    await user.upload(logInput(), cast());
    await user.click(submitButton());

    expect(await screen.findByText(/must be valid utf-8 text/i)).toBeInTheDocument();
    expect(submitButton()).toBeEnabled();
  });
});

describe('UploadWorkspace media review', () => {
  const artifacts = {
    video: { durationSeconds: 30, width: 1280, height: 720, frameRate: 30, hasAudio: true },
    frames: [{ index: 0, timestampSeconds: 0, fileName: 'frame_0001.jpg' }],
    audioIntervals: [],
    technicalEvents: [],
    appliedReferenceOffsetSeconds: null,
  };

  const submitFiles = async (api: WorkspaceApi) => {
    const user = userEvent.setup({ applyAccept: false });
    render(<UploadWorkspace api={api} pollIntervalMs={5} />);
    await user.upload(videoInput(), mp4());
    await user.upload(logInput(), cast());
    await user.click(submitButton());
  };

  it('shows the processing stage next to the progress bar', async () => {
    const api = makeApi({
      uploadSession: vi.fn().mockResolvedValue({
        ...baseSession,
        status: 'Processing',
        progressPercent: 45,
        processingStage: 'Extracting keyframes',
      }),
      fetchStatus: vi.fn().mockResolvedValue({
        ...baseSession,
        status: 'Processing',
        progressPercent: 45,
        processingStage: 'Extracting keyframes',
      }),
    });

    await submitFiles(api);

    expect(await screen.findByTestId('processing-stage')).toHaveTextContent('Extracting keyframes');
  });

  it('shows the error message when processing fails', async () => {
    const api = makeApi({
      uploadSession: vi.fn().mockResolvedValue({
        ...baseSession,
        status: 'Failed',
        processingStage: 'Failed',
        errorMessage: 'ffprobe exited with code 1: Invalid data found',
      }),
    });

    await submitFiles(api);

    expect(await screen.findByTestId('error-message')).toHaveTextContent('ffprobe exited with code 1');
  });

  it('fetches artifacts automatically and renders the review panel when ready', async () => {
    const api = makeApi({
      uploadSession: vi.fn().mockResolvedValue({ ...baseSession, status: 'ReadyForReview', progressPercent: 100 }),
      fetchArtifacts: vi.fn().mockResolvedValue(artifacts),
    });

    await submitFiles(api);

    expect(await screen.findByRole('region', { name: /session review/i })).toBeInTheDocument();
    expect(api.fetchArtifacts).toHaveBeenCalledWith(baseSession.id, expect.any(AbortSignal));
    expect(screen.getByTestId('meta-resolution')).toHaveTextContent('1280×720');
  });

  it('reports an error when artifacts cannot be loaded', async () => {
    const api = makeApi({
      uploadSession: vi.fn().mockResolvedValue({ ...baseSession, status: 'ReadyForReview', progressPercent: 100 }),
      fetchArtifacts: vi.fn().mockRejectedValue(new Error('Artifacts are not available yet.')),
    });

    await submitFiles(api);

    expect(await screen.findByText(/artifacts are not available yet/i)).toBeInTheDocument();
    expect(screen.queryByRole('region', { name: /session review/i })).not.toBeInTheDocument();
  });
});
