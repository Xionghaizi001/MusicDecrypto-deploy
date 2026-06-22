export type ApiAuth = {
  apiKey: string;
  apiBaseUrl: string;
};

export type BackendHealth = {
  status: string;
  service: string;
  utc: string;
};

export type JobStatus = 'Queued' | 'Running' | 'Completed' | 'Failed';

export type JobRecord = {
  id: string;
  tusFileId: string;
  originalFileName: string;
  inputPath: string;
  outputPath: string | null;
  status: JobStatus;
  error: string | null;
  log: string | null;
  createdAt: string;
  updatedAt: string;
  startedAt: string | null;
  completedAt: string | null;
};

export type JobDeleteResult = {
  jobId: string;
  deletedPaths: string[];
  missingPaths: string[];
};

export type UploadProgress = {
  id: string;
  name: string;
  percentage: number;
  bytesUploaded: number;
  bytesTotal: number;
};
