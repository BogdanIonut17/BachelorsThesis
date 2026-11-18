import type { ProcessingSessionDto } from './types';

export class ApiError extends Error {
  readonly status: number;

  constructor(message: string, status: number) {
    super(message);
    this.name = 'ApiError';
    this.status = status;
  }
}

const extractProblemMessage = (body: unknown, fallback: string): string => {
  if (body && typeof body === 'object') {
    const problem = body as { errors?: Record<string, string[]>; title?: string; detail?: string };
    if (problem.errors) {
      const messages = Object.values(problem.errors).flat();
      if (messages.length > 0) {
        return messages.join(' ');
      }
    }
    if (problem.detail) {
      return problem.detail;
    }
    if (problem.title) {
      return problem.title;
    }
  }
  return fallback;
};

const parseJson = (text: string): unknown => {
  try {
    return text ? JSON.parse(text) : null;
  } catch {
    return null;
  }
};

export interface UploadInput {
  videoFile: File;
  logFile: File;
  manualReferenceOffsetSeconds: number | null;
}

export const uploadSession = (
  input: UploadInput,
  onProgress: (percent: number) => void,
): Promise<ProcessingSessionDto> =>
  new Promise((resolve, reject) => {
    const form = new FormData();
    form.append('videoFile', input.videoFile);
    form.append('logFile', input.logFile);
    if (input.manualReferenceOffsetSeconds !== null) {
      form.append('manualReferenceOffsetSeconds', String(input.manualReferenceOffsetSeconds));
    }

    const request = new XMLHttpRequest();
    request.open('POST', '/api/sessions/upload');

    request.upload.onprogress = (event) => {
      if (event.lengthComputable && event.total > 0) {
        onProgress(Math.round((event.loaded / event.total) * 100));
      }
    };

    request.onload = () => {
      const body = parseJson(request.responseText);
      if (request.status >= 200 && request.status < 300 && body) {
        onProgress(100);
        resolve(body as ProcessingSessionDto);
        return;
      }
      reject(new ApiError(extractProblemMessage(body, `Upload failed (HTTP ${request.status}).`), request.status));
    };

    request.onerror = () => reject(new ApiError('Network error while uploading files.', 0));
    request.onabort = () => reject(new ApiError('Upload was cancelled.', 0));

    request.send(form);
  });

export const fetchStatus = async (id: string, signal?: AbortSignal): Promise<ProcessingSessionDto> => {
  const response = await fetch(`/api/sessions/${id}/status`, { signal });
  const body = parseJson(await response.text());
  if (!response.ok || !body) {
    throw new ApiError(extractProblemMessage(body, `Status request failed (HTTP ${response.status}).`), response.status);
  }
  return body as ProcessingSessionDto;
};

export const setReferencePoint = async (
  id: string,
  manualReferenceOffsetSeconds: number,
): Promise<ProcessingSessionDto> => {
  const response = await fetch(`/api/sessions/${id}/reference-point`, {
    method: 'PATCH',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ manualReferenceOffsetSeconds }),
  });
  const body = parseJson(await response.text());
  if (!response.ok || !body) {
    throw new ApiError(
      extractProblemMessage(body, `Setting reference point failed (HTTP ${response.status}).`),
      response.status,
    );
  }
  return body as ProcessingSessionDto;
};
