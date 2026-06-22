import type { ApiAuth, BackendHealth, JobDeleteResult, JobRecord } from '../types/backend';
import { saveBlob } from './file-system';

const defaultApiBaseUrl = normalizeApiBaseUrl(import.meta.env.VITE_API_BASE_URL ?? '');

export type JobDownload = {
  blob: Blob;
  filename: string;
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

export async function fetchJobDownload(id: string, auth: ApiAuth): Promise<JobDownload> {
  const response = await fetch(buildApiUrl(`/api/jobs/${id}/download`, auth), {
    headers: authHeaders(auth)
  });

  if (!response.ok) {
    throw new Error(`Download failed: ${response.status}`);
  }

  const blob = await response.blob();
  const filename = normalizeDownloadedFilename(getDownloadFilename(response) ?? `${id}.bin`, id);

  return {
    blob,
    filename
  };
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
