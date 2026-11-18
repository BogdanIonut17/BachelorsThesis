export const VIDEO_EXTENSIONS = ['.mp4', '.mkv', '.webm'] as const;
export const LOG_EXTENSIONS = ['.cast', '.jsonl', '.log'] as const;

export const MAX_VIDEO_BYTES = 524_288_000;
export const MAX_LOG_BYTES = 26_214_400;

const extensionOf = (fileName: string): string => {
  const index = fileName.lastIndexOf('.');
  return index < 0 ? '' : fileName.slice(index).toLowerCase();
};

const validate = (
  file: File,
  allowed: readonly string[],
  maxBytes: number,
  label: string,
  maxLabel: string,
): string | null => {
  if (!allowed.includes(extensionOf(file.name))) {
    return `${label} must be one of: ${allowed.join(', ')}.`;
  }
  if (file.size === 0) {
    return `${label} must not be empty.`;
  }
  if (file.size > maxBytes) {
    return `${label} exceeds the ${maxLabel} limit.`;
  }
  return null;
};

export const validateVideoFile = (file: File): string | null =>
  validate(file, VIDEO_EXTENSIONS, MAX_VIDEO_BYTES, 'Screen recording', '500 MB');

export const validateLogFile = (file: File): string | null =>
  validate(file, LOG_EXTENSIONS, MAX_LOG_BYTES, 'Execution log', '25 MB');

export const parseOffset = (raw: string): number | null | undefined => {
  const trimmed = raw.trim();
  if (trimmed === '') {
    return null;
  }
  const value = Number(trimmed);
  return Number.isFinite(value) ? value : undefined;
};
