interface ProgressBarProps {
  label: string;
  percent: number;
  tone?: 'indigo' | 'emerald';
}

export function ProgressBar({ label, percent, tone = 'indigo' }: ProgressBarProps) {
  const clamped = Math.min(100, Math.max(0, Math.round(percent)));
  const barColor = tone === 'emerald' ? 'bg-emerald-500' : 'bg-indigo-500';

  return (
    <div className="flex flex-col gap-1">
      <div className="flex justify-between text-sm text-slate-700">
        <span>{label}</span>
        <span>{clamped}%</span>
      </div>
      <div
        role="progressbar"
        aria-label={label}
        aria-valuemin={0}
        aria-valuemax={100}
        aria-valuenow={clamped}
        className="h-2.5 w-full overflow-hidden rounded-full bg-slate-200"
      >
        <div className={`h-full transition-all duration-500 ${barColor}`} style={{ width: `${clamped}%` }} />
      </div>
    </div>
  );
}
