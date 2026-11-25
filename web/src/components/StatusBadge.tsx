import type { CollectionRole, DocumentStatus } from '../api/types';
import { fa } from '../i18n/fa';

const statusClassMap: Record<DocumentStatus, string> = {
  Uploaded: 'badge-uploaded',
  Processing: 'badge-processing',
  Ready: 'badge-ready',
  Failed: 'badge-failed',
};

export function StatusBadge({ status }: { status: DocumentStatus }) {
  return <span className={`badge ${statusClassMap[status]}`}>{fa.documentStatuses[status]}</span>;
}

export function RoleBadge({ role }: { role: CollectionRole }) {
  return <span className="badge badge-role">{fa.roles[role]}</span>;
}
