import type { SensitiveMatch } from '../types';

interface PrivacyAlertProps {
  secrets: SensitiveMatch[];
}

export function PrivacyAlert({ secrets }: PrivacyAlertProps) {
  if (secrets.length === 0) {
    return null;
  }

  return (
    <section
      role="alert"
      aria-labelledby="privacy-alert-title"
      className="rounded-lg border border-amber-300 bg-amber-50 p-4"
    >
      <h3 id="privacy-alert-title" className="text-base font-semibold text-amber-900">
        Privacy Alert: {secrets.length} sensitive {secrets.length === 1 ? 'item' : 'items'} detected
      </h3>
      <p className="mt-1 text-sm text-amber-800">
        These values were masked in a sanitized copy of the log before it can be shared with any external AI service.
      </p>
      <div className="mt-3 overflow-x-auto">
        <table className="w-full text-left text-sm">
          <thead>
            <tr className="border-b border-amber-300 text-amber-900">
              <th scope="col" className="py-1 pr-4 font-medium">
                Line
              </th>
              <th scope="col" className="py-1 pr-4 font-medium">
                Type
              </th>
              <th scope="col" className="py-1 font-medium">
                Masked preview
              </th>
            </tr>
          </thead>
          <tbody>
            {secrets.map((secret, index) => (
              <tr key={`${secret.lineNumber}-${secret.ruleName}-${index}`} className="border-b border-amber-200 last:border-0">
                <td className="py-1 pr-4 tabular-nums">{secret.lineNumber}</td>
                <td className="py-1 pr-4">{secret.ruleName}</td>
                <td className="py-1 font-mono">{secret.maskedPreview}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </section>
  );
}
