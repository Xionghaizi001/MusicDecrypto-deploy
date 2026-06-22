import Uppy from '@uppy/core';
import Tus, { type TusBody } from '@uppy/tus';
import { authHeaders, buildApiUrl } from './api';
import type { ApiAuth, UploadProgress } from '../types/backend';

type UploadMeta = {
  filename?: string;
};

export async function uploadFiles(
  files: File[],
  auth: ApiAuth,
  onProgress: (items: UploadProgress[]) => void
): Promise<void> {
  const progressItems = files.map((file, index) => ({
    id: `${index}-${file.name}`,
    name: file.name,
    percentage: 0,
    bytesUploaded: 0,
    bytesTotal: file.size
  }));

  onProgress(progressItems);

  const failures: string[] = [];

  for (const [index, file] of files.entries()) {
    try {
      await uploadFile(file, index, auth, (item) => {
        progressItems[index] = item;
        onProgress([...progressItems]);
      });
    } catch (err) {
      failures.push(`${file.name}: ${err instanceof Error ? err.message : '上传失败'}`);
    }
  }

  if (failures.length > 0) {
    throw new Error(failures.join(', '));
  }
}

async function uploadFile(
  file: File,
  index: number,
  auth: ApiAuth,
  onProgress: (item: UploadProgress) => void
): Promise<void> {
  const uppy = new Uppy<UploadMeta, TusBody>({
    autoProceed: false,
    restrictions: {
      maxNumberOfFiles: 1
    }
  }).use(Tus, {
    endpoint: buildApiUrl('/files', auth),
    headers: authHeaders(auth),
    allowedMetaFields: ['filename'],
    retryDelays: [0, 1000, 3000, 5000]
  });

  const emitProgress = () => {
    const uppyFile = uppy.getFiles()[0];

    onProgress({
      id: `${index}-${file.name}`,
      name: file.name,
      percentage: uppyFile?.progress.percentage ?? 0,
      bytesUploaded: uppyFile?.progress.bytesUploaded || 0,
      bytesTotal: uppyFile?.progress.bytesTotal ?? file.size
    });
  };

  uppy.on('upload-progress', emitProgress);
  uppy.on('upload-success', emitProgress);
  uppy.on('upload-error', emitProgress);

  try {
    uppy.addFile({
      name: file.name,
      type: file.type,
      data: file,
      meta: {
        filename: file.name
      }
    });

    emitProgress();
    const result = await uppy.upload();

    if (result?.failed && result.failed.length > 0) {
      throw new Error(result.failed.map((file) => String(file.error ?? file.name)).join(', '));
    }
  } finally {
    uppy.destroy();
  }
}
