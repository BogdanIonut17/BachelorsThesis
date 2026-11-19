import { useId, useRef, useState } from 'react';
import type { DragEvent } from 'react';

interface FileDropzoneProps {
  label: string;
  hint: string;
  accept: string;
  testId: string;
  file: File | null;
  error: string | null;
  disabled?: boolean;
  onFileSelected: (file: File) => void;
}

const formatSize = (bytes: number): string => {
  if (bytes >= 1_048_576) {
    return `${(bytes / 1_048_576).toFixed(1)} MB`;
  }
  return `${Math.max(1, Math.round(bytes / 1024))} KB`;
};

export function FileDropzone({
  label,
  hint,
  accept,
  testId,
  file,
  error,
  disabled = false,
  onFileSelected,
}: FileDropzoneProps) {
  const inputId = useId();
  const errorId = `${inputId}-error`;
  const inputRef = useRef<HTMLInputElement>(null);
  const [isDragging, setIsDragging] = useState(false);

  const handleDrop = (event: DragEvent<HTMLDivElement>) => {
    event.preventDefault();
    setIsDragging(false);
    if (disabled) {
      return;
    }
    const dropped = event.dataTransfer.files?.[0];
    if (dropped) {
      onFileSelected(dropped);
    }
  };

  const borderClass = error
    ? 'border-red-400 bg-red-50'
    : isDragging
      ? 'border-indigo-500 bg-indigo-50'
      : 'border-slate-300 bg-white hover:border-indigo-400';

  return (
    <div className="flex flex-col gap-2">
      <label htmlFor={inputId} className="text-sm font-medium text-slate-700">
        {label}
      </label>
      <div
        data-testid={testId}
        onDragOver={(event) => {
          event.preventDefault();
          if (!disabled) {
            setIsDragging(true);
          }
        }}
        onDragLeave={() => setIsDragging(false)}
        onDrop={handleDrop}
        className={`flex flex-col items-center justify-center gap-2 rounded-lg border-2 border-dashed p-6 text-center transition-colors ${borderClass}`}
      >
        <p className="text-sm text-slate-600">
          {file ? (
            <span className="font-medium text-slate-900">
              {file.name} <span className="font-normal text-slate-500">({formatSize(file.size)})</span>
            </span>
          ) : (
            'Drag and drop a file here, or'
          )}
        </p>
        <button
          type="button"
          disabled={disabled}
          onClick={() => inputRef.current?.click()}
          className="rounded-md border border-slate-300 bg-white px-3 py-1.5 text-sm font-medium text-slate-700 shadow-sm hover:bg-slate-50 disabled:cursor-not-allowed disabled:opacity-50"
        >
          {file ? 'Replace file' : 'Browse files'}
        </button>
        <p className="text-xs text-slate-500">{hint}</p>
        <input
          ref={inputRef}
          id={inputId}
          type="file"
          accept={accept}
          disabled={disabled}
          aria-invalid={error ? true : undefined}
          aria-describedby={error ? errorId : undefined}
          className="sr-only"
          onChange={(event) => {
            const selected = event.target.files?.[0];
            if (selected) {
              onFileSelected(selected);
            }
            event.target.value = '';
          }}
        />
      </div>
      {error && (
        <p id={errorId} role="alert" className="text-sm text-red-600">
          {error}
        </p>
      )}
    </div>
  );
}
