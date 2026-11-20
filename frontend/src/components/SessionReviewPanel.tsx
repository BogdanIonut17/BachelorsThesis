import { useMemo, useRef, useState } from 'react';
import { getFrameUrl, getVideoUrl } from '../api';
import type { SessionArtifactsDto, TechnicalEventCategory } from '../types';

interface SessionReviewPanelProps {
  sessionId: string;
  artifacts: SessionArtifactsDto;
}

type EventFilter = 'anchorable' | 'all';

const CATEGORY_STYLES: Record<TechnicalEventCategory, string> = {
  Command: 'bg-indigo-100 text-indigo-800',
  Build: 'bg-sky-100 text-sky-800',
  Container: 'bg-cyan-100 text-cyan-800',
  Error: 'bg-red-100 text-red-800',
  ExitCode: 'bg-amber-100 text-amber-800',
  Output: 'bg-slate-100 text-slate-700',
};

export const formatTimestamp = (seconds: number): string => {
  const total = Math.max(0, Math.floor(seconds));
  const minutes = Math.floor(total / 60);
  const rest = total % 60;
  return `${String(minutes).padStart(2, '0')}:${String(rest).padStart(2, '0')}`;
};

export default function SessionReviewPanel({ sessionId, artifacts }: SessionReviewPanelProps) {
  const videoRef = useRef<HTMLVideoElement>(null);
  const [filter, setFilter] = useState<EventFilter>('anchorable');

  const { video, frames, audioIntervals, technicalEvents, appliedReferenceOffsetSeconds } = artifacts;
  const offset = appliedReferenceOffsetSeconds ?? 0;

  const visibleEvents = useMemo(
    () => (filter === 'anchorable' ? technicalEvents.filter((event) => event.isAnchorable) : technicalEvents),
    [filter, technicalEvents],
  );

  const seekTo = (seconds: number) => {
    if (videoRef.current) {
      videoRef.current.currentTime = Math.max(0, seconds);
    }
  };

  const duration = video.durationSeconds > 0 ? video.durationSeconds : 1;

  return (
    <section aria-label="Session review" className="flex flex-col gap-6 rounded-lg border border-slate-200 bg-white p-4">
      <h2 className="text-lg font-semibold text-slate-900">Review</h2>

      <video
        ref={videoRef}
        data-testid="review-video"
        controls
        preload="metadata"
        src={getVideoUrl(sessionId)}
        className="w-full rounded-md bg-black"
      />

      <dl aria-label="Video metadata" className="grid grid-cols-2 gap-x-4 gap-y-1 text-sm sm:grid-cols-4">
        <div>
          <dt className="text-slate-500">Duration</dt>
          <dd data-testid="meta-duration" className="font-medium text-slate-900">
            {formatTimestamp(video.durationSeconds)}
          </dd>
        </div>
        <div>
          <dt className="text-slate-500">Resolution</dt>
          <dd data-testid="meta-resolution" className="font-medium text-slate-900">
            {video.width}×{video.height}
          </dd>
        </div>
        <div>
          <dt className="text-slate-500">Frame rate</dt>
          <dd data-testid="meta-fps" className="font-medium text-slate-900">
            {video.frameRate.toFixed(2)} fps
          </dd>
        </div>
        <div>
          <dt className="text-slate-500">Audio</dt>
          <dd data-testid="meta-audio" className="font-medium text-slate-900">
            {video.hasAudio ? 'Audio track present' : 'No audio track'}
          </dd>
        </div>
      </dl>

      <div className="flex flex-col gap-2">
        <h3 className="text-sm font-semibold text-slate-800">Audio activity</h3>
        {audioIntervals.length > 0 ? (
          <div role="group" aria-label="Audio activity timeline" className="flex h-6 w-full overflow-hidden rounded-md border border-slate-200">
            {audioIntervals.map((interval) => {
              const label = `${interval.isActive ? 'Active' : 'Silent'} audio ${formatTimestamp(interval.startSeconds)} to ${formatTimestamp(interval.endSeconds)}`;
              return (
                <button
                  key={`${interval.startSeconds}-${interval.endSeconds}`}
                  type="button"
                  aria-label={label}
                  title={label}
                  onClick={() => seekTo(interval.startSeconds)}
                  style={{ width: `${((interval.endSeconds - interval.startSeconds) / duration) * 100}%` }}
                  className={`h-full ${interval.isActive ? 'bg-emerald-500 hover:bg-emerald-400' : 'bg-slate-200 hover:bg-slate-300'}`}
                />
              );
            })}
          </div>
        ) : (
          <p className="text-sm text-slate-500">No audio activity data (the recording has no audio track).</p>
        )}
      </div>

      <div className="flex flex-col gap-2">
        <h3 className="text-sm font-semibold text-slate-800">Extracted keyframes</h3>
        <ul aria-label="Extracted keyframes" className="flex gap-3 overflow-x-auto pb-2">
          {frames.map((frame) => (
            <li key={frame.fileName} className="shrink-0">
              <button
                type="button"
                onClick={() => seekTo(frame.timestampSeconds)}
                className="flex flex-col items-center gap-1 rounded-md border border-slate-200 p-1 hover:border-indigo-400"
              >
                <img
                  src={getFrameUrl(sessionId, frame.fileName)}
                  alt={`Frame at ${formatTimestamp(frame.timestampSeconds)}`}
                  loading="lazy"
                  className="h-20 w-36 rounded object-cover"
                />
                <span className="text-xs tabular-nums text-slate-600">{formatTimestamp(frame.timestampSeconds)}</span>
              </button>
            </li>
          ))}
        </ul>
      </div>

      <div className="flex flex-col gap-2">
        <div className="flex flex-wrap items-center justify-between gap-2">
          <h3 className="text-sm font-semibold text-slate-800">Technical events</h3>
          <div role="group" aria-label="Event filter" className="inline-flex overflow-hidden rounded-md border border-slate-300 text-sm">
            <button
              type="button"
              aria-pressed={filter === 'anchorable'}
              onClick={() => setFilter('anchorable')}
              className={`px-3 py-1 ${filter === 'anchorable' ? 'bg-indigo-600 text-white' : 'bg-white text-slate-700'}`}
            >
              Anchorable only
            </button>
            <button
              type="button"
              aria-pressed={filter === 'all'}
              onClick={() => setFilter('all')}
              className={`px-3 py-1 ${filter === 'all' ? 'bg-indigo-600 text-white' : 'bg-white text-slate-700'}`}
            >
              All events
            </button>
          </div>
        </div>
        {offset !== 0 && (
          <p className="text-xs text-slate-500">
            Event timestamps are shifted by the reference offset of {offset}s when seeking the video.
          </p>
        )}
        {visibleEvents.length === 0 ? (
          <p className="text-sm text-slate-500">No events to show.</p>
        ) : (
          <ul aria-label="Technical events" className="max-h-72 divide-y divide-slate-100 overflow-y-auto rounded-md border border-slate-200">
            {visibleEvents.map((event) => (
              <li key={event.index}>
                <button
                  type="button"
                  onClick={() => seekTo(event.timestampSeconds + offset)}
                  className="flex w-full items-start gap-3 px-3 py-2 text-left hover:bg-slate-50"
                >
                  <span className={`shrink-0 rounded px-2 py-0.5 text-xs font-medium ${CATEGORY_STYLES[event.category]}`}>
                    {event.category}
                  </span>
                  <span className="shrink-0 text-xs tabular-nums text-slate-500">{formatTimestamp(event.timestampSeconds)}</span>
                  <code className="min-w-0 flex-1 truncate font-mono text-xs text-slate-800">{event.text}</code>
                </button>
              </li>
            ))}
          </ul>
        )}
      </div>
    </section>
  );
}
