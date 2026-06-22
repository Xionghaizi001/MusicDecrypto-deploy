import type { ApiAuth, BackendHealth, JobDeleteResult, JobRecord } from '../types/backend';
import { saveBlob } from './file-system';

const defaultApiBaseUrl = normalizeApiBaseUrl(import.meta.env.VITE_API_BASE_URL ?? '');

export type JobDownload = {
  blob: Blob;
  filename: string;
};

export type JobDownloadProgress = {
  bytesDownloaded: number;
  bytesTotal: number | null;
  percentage: number | null;
};

export async function fetchHealth(auth: ApiAuth): Promise<BackendHealth> {
  return request<BackendHealth>('/healthz', auth);
}

export async function fetchJobs(auth: ApiAuth): Promise<JobRecord[]> {
  return request<JobRecord[]>('/api/jobs', auth);
}

export async function downloadJob(id: string, auth: ApiAuth): Promise<void> {
  const download = await fetchJobDownload(id, auth);
  await saveBlob(download.blob, download.filename);
}

export async function fetchJobDownload(
  id: string,
  auth: ApiAuth,
  onProgress?: (progress: JobDownloadProgress) => void
): Promise<JobDownload> {
  const response = await fetch(buildApiUrl(`/api/jobs/${id}/download`, auth), {
    headers: authHeaders(auth)
  });

  if (!response.ok) {
    throw new Error(`Download failed: ${response.status}`);
  }

  const blob = await readDownloadBlob(response, onProgress);
  const filename = normalizeDownloadedFilename(getDownloadFilename(response) ?? `${id}.bin`, id);

  return {
    blob,
    filename
  };
}

async function readDownloadBlob(
  response: Response,
  onProgress?: (progress: JobDownloadProgress) => void
): Promise<Blob> {
  const bytesTotal = parseContentLength(response.headers.get('content-length'));

  if (!response.body) {
    const blob = await response.blob();
    onProgress?.({
      bytesDownloaded: blob.size,
      bytesTotal: blob.size,
      percentage: 100
    });
    return blob;
  }

  const reader = response.body.getReader();
  const chunks: BlobPart[] = [];
  let bytesDownloaded = 0;

  while (true) {
    const { done, value } = await reader.read();

    if (done) {
      break;
    }

    const chunk = new Uint8Array(value.byteLength);
    chunk.set(value);
    chunks.push(chunk);
    bytesDownloaded += value.byteLength;
    onProgress?.({
      bytesDownloaded,
      bytesTotal,
      percentage: bytesTotal ? (bytesDownloaded / bytesTotal) * 100 : null
    });
  }

  if (bytesTotal === null) {
    onProgress?.({
      bytesDownloaded,
      bytesTotal,
      percentage: 100
    });
  }

  return new Blob(chunks, {
    type: response.headers.get('content-type') ?? 'application/octet-stream'
  });
}

function parseContentLength(value: string | null): number | null {
  if (!value) {
    return null;
  }

  const parsed = Number(value);
  return Number.isFinite(parsed) && parsed > 0 ? parsed : null;
}

export async function deleteJob(id: string, auth: ApiAuth): Promise<JobDeleteResult> {
  const response = await fetch(buildApiUrl(`/api/jobs/${id}/delete`, auth), {
    method: 'POST',
    headers: authHeaders(auth)
  });

  if (!response.ok) {
    const text = await response.text();
    throw new Error(text || `Delete failed: ${response.status}`);
  }

  return response.json() as Promise<JobDeleteResult>;
}

export function buildApiUrl(path: string, auth: ApiAuth): string {
  const apiBaseUrl = auth.apiBaseUrl || defaultApiBaseUrl;
  return `${apiBaseUrl}${path.startsWith('/') ? path : `/${path}`}`;
}

export function authHeaders(auth: ApiAuth): Record<string, string> {
  return auth.apiKey ? { 'X-Api-Key': auth.apiKey } : {};
}

async function request<T>(path: string, auth: ApiAuth): Promise<T> {
  const response = await fetch(buildApiUrl(path, auth), {
    headers: {
      Accept: 'application/json',
      ...authHeaders(auth)
    }
  });

  if (!response.ok) {
    throw new Error(`Request failed: ${response.status}`);
  }

  return response.json() as Promise<T>;
}

function trimTrailingSlash(value: string): string {
  return value.replace(/\/+$/, '');
}

export function normalizeApiBaseUrl(value: string): string {
  const trimmed = trimTrailingSlash(value.trim());

  if (!trimmed) {
    return '';
  }

  if (/^[a-z][a-z\d+\-.]*:\/\//i.test(trimmed) || trimmed.startsWith('/')) {
    return trimmed;
  }

  return `${window.location.protocol}//${trimmed}`;
}

function getDownloadFilename(response: Response): string | null {
  const disposition = response.headers.get('content-disposition');

  if (!disposition) {
    return null;
  }

  const encodedMatch = disposition.match(/filename\*=UTF-8''"?([^";]+)"?/i);
  if (encodedMatch) {
    return decodeURIComponent(encodedMatch[1]);
  }

  const fallbackMatch = disposition.match(/filename="?([^";]+)"?/i);
  return fallbackMatch ? fallbackMatch[1] : null;
}

function normalizeDownloadedFilename(filename: string, jobId: string): string {
  if (!filename.toLowerCase().startsWith(jobId.toLowerCase())) {
    return filename;
  }

  const trimmed = filename.slice(jobId.length).replace(/^[-_\s.]+/, '');
  return trimmed || filename;
}
