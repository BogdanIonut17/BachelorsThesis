import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import SessionReviewPanel, { formatTimestamp } from '../components/SessionReviewPanel';
import type { SessionArtifactsDto } from '../types';

const SESSION_ID = '22222222-2222-2222-2222-222222222222';

const makeArtifacts = (overrides: Partial<SessionArtifactsDto> = {}): SessionArtifactsDto => ({
  video: { durationSeconds: 125, width: 1920, height: 1080, frameRate: 29.97, hasAudio: true },
  frames: [
    { index: 0, timestampSeconds: 0, fileName: 'frame_0001.jpg' },
    { index: 1, timestampSeconds: 65, fileName: 'frame_0002.jpg' },
  ],
  audioIntervals: [
    { startSeconds: 0, endSeconds: 50, isActive: true },
    { startSeconds: 50, endSeconds: 125, isActive: false },
  ],
  technicalEvents: [
    { index: 0, timestampSeconds: 3, eventType: 'o', category: 'Command', text: '$ docker build .', isAnchorable: true },
    { index: 1, timestampSeconds: 20, eventType: 'o', category: 'Output', text: 'just some chatter', isAnchorable: false },
    { index: 2, timestampSeconds: 42, eventType: 'o', category: 'Error', text: 'error CS1002: ; expected', isAnchorable: false },
    { index: 3, timestampSeconds: 70.9, eventType: 'x', category: 'ExitCode', text: 'exit code 0', isAnchorable: true },
  ],
  appliedReferenceOffsetSeconds: null,
  ...overrides,
});

const getVideo = () => screen.getByTestId('review-video') as HTMLVideoElement;

describe('SessionReviewPanel', () => {
  it('renders the video player bound to the session video endpoint with metadata', () => {
    render(<SessionReviewPanel sessionId={SESSION_ID} artifacts={makeArtifacts()} />);

    const video = getVideo();
    expect(video).toHaveAttribute('controls');
    expect(video).toHaveAttribute('src', `/api/sessions/${SESSION_ID}/video`);

    expect(screen.getByTestId('meta-duration')).toHaveTextContent('02:05');
    expect(screen.getByTestId('meta-resolution')).toHaveTextContent('1920×1080');
    expect(screen.getByTestId('meta-fps')).toHaveTextContent('29.97 fps');
    expect(screen.getByTestId('meta-audio')).toHaveTextContent('Audio track present');
  });

  it('reports a missing audio track', () => {
    const artifacts = makeArtifacts({
      video: { durationSeconds: 10, width: 640, height: 360, frameRate: 25, hasAudio: false },
      audioIntervals: [],
    });
    render(<SessionReviewPanel sessionId={SESSION_ID} artifacts={artifacts} />);

    expect(screen.getByTestId('meta-audio')).toHaveTextContent('No audio track');
    expect(screen.queryByRole('group', { name: /audio activity timeline/i })).not.toBeInTheDocument();
  });

  it('renders keyframe thumbnails with formatted timestamps and seeks on click', async () => {
    const user = userEvent.setup();
    render(<SessionReviewPanel sessionId={SESSION_ID} artifacts={makeArtifacts()} />);

    const strip = screen.getByRole('list', { name: /extracted keyframes/i });
    const images = within(strip).getAllByRole('img');
    expect(images).toHaveLength(2);
    expect(images[1]).toHaveAttribute('src', `/api/sessions/${SESSION_ID}/frames/frame_0002.jpg`);
    expect(within(strip).getByText('01:05')).toBeInTheDocument();

    await user.click(within(strip).getByRole('button', { name: /frame at 01:05/i }));

    expect(getVideo().currentTime).toBe(65);
  });

  it('renders the audio activity timeline proportionally and seeks on click', async () => {
    const user = userEvent.setup();
    render(<SessionReviewPanel sessionId={SESSION_ID} artifacts={makeArtifacts()} />);

    const timeline = screen.getByRole('group', { name: /audio activity timeline/i });
    const segments = within(timeline).getAllByRole('button');
    expect(segments).toHaveLength(2);
    expect(segments[0]).toHaveStyle({ width: '40%' });
    expect(segments[1]).toHaveStyle({ width: '60%' });

    await user.click(within(timeline).getByRole('button', { name: /silent audio 00:50 to 02:05/i }));

    expect(getVideo().currentTime).toBe(50);
  });

  it('shows only anchorable events by default and toggles to all events', async () => {
    const user = userEvent.setup();
    render(<SessionReviewPanel sessionId={SESSION_ID} artifacts={makeArtifacts()} />);

    const list = () => screen.getByRole('list', { name: /technical events/i });
    expect(within(list()).getAllByRole('button')).toHaveLength(2);
    expect(within(list()).getByText('$ docker build .')).toBeInTheDocument();
    expect(within(list()).queryByText('just some chatter')).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: /anchorable only/i })).toHaveAttribute('aria-pressed', 'true');

    await user.click(screen.getByRole('button', { name: /all events/i }));

    expect(within(list()).getAllByRole('button')).toHaveLength(4);
    expect(within(list()).getByText('just some chatter')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /all events/i })).toHaveAttribute('aria-pressed', 'true');

    await user.click(screen.getByRole('button', { name: /anchorable only/i }));

    expect(within(list()).getAllByRole('button')).toHaveLength(2);
  });

  it('renders category badges, timestamps and monospace previews for events', async () => {
    const user = userEvent.setup();
    render(<SessionReviewPanel sessionId={SESSION_ID} artifacts={makeArtifacts()} />);

    await user.click(screen.getByRole('button', { name: /all events/i }));

    const list = screen.getByRole('list', { name: /technical events/i });
    for (const category of ['Command', 'Output', 'Error', 'ExitCode']) {
      expect(within(list).getByText(category)).toBeInTheDocument();
    }
    expect(within(list).getByText('01:10')).toBeInTheDocument();
    expect(within(list).getByText('error CS1002: ; expected').tagName).toBe('CODE');
  });

  it('seeks the video to the event timestamp when no reference offset is applied', async () => {
    const user = userEvent.setup();
    render(<SessionReviewPanel sessionId={SESSION_ID} artifacts={makeArtifacts()} />);

    await user.click(screen.getByRole('button', { name: /\$ docker build \./i }));

    expect(getVideo().currentTime).toBe(3);
  });

  it('adds the applied reference offset when seeking from an event', async () => {
    const user = userEvent.setup();
    render(
      <SessionReviewPanel sessionId={SESSION_ID} artifacts={makeArtifacts({ appliedReferenceOffsetSeconds: 4.5 })} />,
    );

    await user.click(screen.getByRole('button', { name: /\$ docker build \./i }));

    expect(getVideo().currentTime).toBe(7.5);
    expect(screen.getByText(/shifted by the reference offset of 4\.5s/i)).toBeInTheDocument();
  });

  it('never seeks to a negative time when the offset is negative', async () => {
    const user = userEvent.setup();
    render(
      <SessionReviewPanel sessionId={SESSION_ID} artifacts={makeArtifacts({ appliedReferenceOffsetSeconds: -10 })} />,
    );

    await user.click(screen.getByRole('button', { name: /\$ docker build \./i }));

    expect(getVideo().currentTime).toBe(0);
  });

  it('shows an empty state when no events match the filter', () => {
    const artifacts = makeArtifacts({
      technicalEvents: [
        { index: 0, timestampSeconds: 1, eventType: 'o', category: 'Output', text: 'noise', isAnchorable: false },
      ],
    });
    render(<SessionReviewPanel sessionId={SESSION_ID} artifacts={artifacts} />);

    expect(screen.getByText(/no events to show/i)).toBeInTheDocument();
  });
});

describe('formatTimestamp', () => {
  it('formats seconds as mm:ss and clamps invalid input', () => {
    expect(formatTimestamp(0)).toBe('00:00');
    expect(formatTimestamp(65.9)).toBe('01:05');
    expect(formatTimestamp(3600)).toBe('60:00');
    expect(formatTimestamp(-4)).toBe('00:00');
  });
});
