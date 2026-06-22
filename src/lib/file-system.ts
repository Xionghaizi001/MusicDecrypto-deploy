type FilePickerWindow = Window & {
  showDirectoryPicker?: (options?: DirectoryPickerOptions) => Promise<FileSystemDirectoryHandle>;
  showOpenFilePicker?: (options?: OpenFilePickerOptions) => Promise<FileSystemFileHandle[]>;
  showSaveFilePicker?: (options?: SaveFilePickerOptions) => Promise<FileSystemFileHandle>;
};

type DirectoryPickerOptions = {
  id?: string;
  mode?: FileSystemAccessMode;
  startIn?: string;
};

type OpenFilePickerOptions = {
  multiple?: boolean;
  types?: FilePickerAcceptType[];
};

type SaveFilePickerOptions = {
  suggestedName?: string;
  types?: FilePickerAcceptType[];
};

type FilePickerAcceptType = {
  description?: string;
  accept: Record<string, string[]>;
};

type FileSystemHandle = FileSystemFileHandle | FileSystemDirectoryHandle;

type FileSystemHandleBase = {
  name: string;
};

type FileSystemFileHandle = FileSystemHandleBase & {
  kind: 'file';
  getFile: () => Promise<File>;
  createWritable?: () => Promise<FileSystemWritableFileStream>;
};

type FileSystemDirectoryHandle = FileSystemHandleBase & {
  kind: 'directory';
  getFileHandle?: (
    name: string,
    options?: { create?: boolean }
  ) => Promise<FileSystemFileHandle>;
  queryPermission?: (descriptor?: FileSystemPermissionDescriptor) => Promise<PermissionState>;
  requestPermission?: (descriptor?: FileSystemPermissionDescriptor) => Promise<PermissionState>;
  removeEntry?: (name: string, options?: { recursive?: boolean }) => Promise<void>;
  values?: () => AsyncIterableIterator<FileSystemHandle>;
};

type FileSystemAccessMode = 'read' | 'readwrite';

type FileSystemPermissionDescriptor = {
  mode?: FileSystemAccessMode;
};

type FileSystemWritableFileStream = {
  write: (data: Blob) => Promise<void>;
  close: () => Promise<void>;
};

export type StorageDirectoryStatus = {
  supported: boolean;
  name: string | null;
  permission: PermissionState | null;
};

export type StorageDirectoryKind = 'source' | 'destination';

export type ScannedStorageFile = {
  name: string;
  relativePath: string;
  extension: string;
  handle: FileSystemFileHandle;
  parentHandle: FileSystemDirectoryHandle;
};

const encryptedMusicExtensions = new Set([
  '.ncm',
  '.tm2',
  '.tm6',
  '.qmc0',
  '.qmc3',
  '.qmc2',
  '.qmc4',
  '.qmc6',
  '.qmc8',
  '.tkm',
  '.qmcogg',
  '.qmcflac',
  '.bkcmp3',
  '.bkcm4a',
  '.bkcwma',
  '.bkcogg',
  '.bkcwav',
  '.bkcape',
  '.bkcflac',
  '.mgg',
  '.mgg1',
  '.mggl',
  '.mflac',
  '.mflac0',
  '.mmp4',
  '.6d7033',
  '.6d3461',
  '.6f6767',
  '.776176',
  '.666c6163',
  '.kgm',
  '.kgma',
  '.vpr',
  '.kwm'
]);

const storageDbName = 'musicdecrypto-file-system';
const storageDbVersion = 1;
const storageStoreName = 'handles';
const storageDirectoryKeys: Record<StorageDirectoryKind, string> = {
  source: 'storage-directory',
  destination: 'storage-destination-directory'
};

export function supportsFileSystemAccess(): boolean {
  return typeof getPickerWindow().showOpenFilePicker === 'function';
}

export function supportsStorageDirectoryAccess(): boolean {
  return typeof getPickerWindow().showDirectoryPicker === 'function';
}

export async function getStorageDirectoryStatus(
  kind: StorageDirectoryKind = 'source'
): Promise<StorageDirectoryStatus> {
  if (!supportsStorageDirectoryAccess()) {
    return emptyStorageDirectoryStatus(false);
  }

  const handle = await readStorageDirectoryHandle(kind);

  if (!handle) {
    return emptyStorageDirectoryStatus(true);
  }

  return {
    supported: true,
    name: handle.name,
    permission: await queryDirectoryPermission(handle)
  };
}

export async function authorizeStorageDirectory(
  kind: StorageDirectoryKind = 'source'
): Promise<StorageDirectoryStatus> {
  const picker = getPickerWindow().showDirectoryPicker;

  if (!picker) {
    return emptyStorageDirectoryStatus(false);
  }

  const handle = await picker({ id: `${kind}-storage`, mode: 'readwrite' });
  const permission = await requestDirectoryPermission(handle);

  if (permission === 'granted') {
    await writeStorageDirectoryHandle(kind, handle);
  }

  return {
    supported: true,
    name: handle.name,
    permission
  };
}

export async function getAuthorizedStorageDirectoryHandle(
  kind: StorageDirectoryKind = 'source'
): Promise<FileSystemDirectoryHandle | null> {
  const handle = await readStorageDirectoryHandle(kind);

  if (!handle) {
    return null;
  }

  const permission = await queryDirectoryPermission(handle);
  return permission === 'granted' ? handle : null;
}

export function getAuthorizedDestinationDirectoryHandle(): Promise<FileSystemDirectoryHandle | null> {
  return getAuthorizedStorageDirectoryHandle('destination');
}

export async function scanStorageDirectoryFiles(): Promise<ScannedStorageFile[]> {
  const handle = await getAuthorizedStorageDirectoryHandle();

  if (!handle) {
    return [];
  }

  const files: ScannedStorageFile[] = [];
  await scanDirectory(handle, '', files);

  return files.sort((left, right) => left.relativePath.localeCompare(right.relativePath));
}

export async function deleteScannedStorageFiles(files: ScannedStorageFile[]): Promise<void> {
  const failures: string[] = [];

  for (const file of files) {
    try {
      if (!file.parentHandle.removeEntry) {
        throw new Error('当前浏览器不支持删除本地文件');
      }

      await file.parentHandle.removeEntry(file.name);
    } catch (err) {
      failures.push(`${file.relativePath}: ${err instanceof Error ? err.message : '删除失败'}`);
    }
  }

  if (failures.length > 0) {
    throw new Error(failures.join(', '));
  }
}

export async function pickLocalFiles(): Promise<File[] | null> {
  const picker = getPickerWindow().showOpenFilePicker;

  if (!picker) {
    return null;
  }

  const handles = await picker({ multiple: true });
  return Promise.all(handles.map((handle) => handle.getFile()));
}

export async function saveBlob(blob: Blob, filename: string): Promise<void> {
  const savePicker = getPickerWindow().showSaveFilePicker;

  if (savePicker) {
    const handle = await savePicker({ suggestedName: filename });
    const writable = await handle.createWritable?.();

    if (writable) {
      await writable.write(blob);
      await writable.close();
      return;
    }
  }

  downloadBlob(blob, filename);
}

export async function saveBlobToStorageDirectory(
  kind: StorageDirectoryKind,
  blob: Blob,
  filename: string
): Promise<string> {
  const directory = await getAuthorizedStorageDirectoryHandle(kind);

  if (!directory) {
    throw new Error(kind === 'destination' ? '保存目录未授权' : '来源目录未授权');
  }

  if (!directory.getFileHandle) {
    throw new Error('当前浏览器不支持写入本地目录');
  }

  const safeFilename = sanitizeStorageFilename(filename);
  const fileHandle = await directory.getFileHandle(safeFilename, { create: true });
  const writable = await fileHandle.createWritable?.();

  if (!writable) {
    throw new Error('当前浏览器不支持写入本地文件');
  }

  await writable.write(blob);
  await writable.close();

  return safeFilename;
}

function downloadBlob(blob: Blob, filename: string): void {
  const objectUrl = URL.createObjectURL(blob);
  const link = document.createElement('a');
  link.href = objectUrl;
  link.download = filename;
  link.click();
  URL.revokeObjectURL(objectUrl);
}

function getPickerWindow(): FilePickerWindow {
  return window as FilePickerWindow;
}

function sanitizeStorageFilename(filename: string): string {
  const sanitized = filename.replace(/[<>:"/\\|?*\u0000-\u001f]/g, '_').trim();
  return sanitized || 'download.bin';
}

function emptyStorageDirectoryStatus(supported: boolean): StorageDirectoryStatus {
  return {
    supported,
    name: null,
    permission: null
  };
}

async function queryDirectoryPermission(
  handle: FileSystemDirectoryHandle
): Promise<PermissionState> {
  return handle.queryPermission?.({ mode: 'readwrite' }) ?? 'prompt';
}

async function requestDirectoryPermission(
  handle: FileSystemDirectoryHandle
): Promise<PermissionState> {
  const currentPermission = await queryDirectoryPermission(handle);

  if (currentPermission === 'granted') {
    return currentPermission;
  }

  return handle.requestPermission?.({ mode: 'readwrite' }) ?? currentPermission;
}

async function readStorageDirectoryHandle(
  kind: StorageDirectoryKind
): Promise<FileSystemDirectoryHandle | null> {
  const db = await openStorageDb();

  return new Promise((resolve, reject) => {
    const transaction = db.transaction(storageStoreName, 'readonly');
    const request = transaction.objectStore(storageStoreName).get(storageDirectoryKeys[kind]);

    request.addEventListener('success', () => {
      resolve((request.result as FileSystemDirectoryHandle | undefined) ?? null);
      db.close();
    });
    request.addEventListener('error', () => {
      reject(request.error);
      db.close();
    });
  });
}

async function writeStorageDirectoryHandle(
  kind: StorageDirectoryKind,
  handle: FileSystemDirectoryHandle
): Promise<void> {
  const db = await openStorageDb();

  return new Promise((resolve, reject) => {
    const transaction = db.transaction(storageStoreName, 'readwrite');
    const request = transaction.objectStore(storageStoreName).put(handle, storageDirectoryKeys[kind]);

    request.addEventListener('success', () => {
      resolve();
      db.close();
    });
    request.addEventListener('error', () => {
      reject(request.error);
      db.close();
    });
  });
}

async function scanDirectory(
  directory: FileSystemDirectoryHandle,
  parentPath: string,
  files: ScannedStorageFile[]
): Promise<void> {
  if (!directory.values) {
    return;
  }

  for await (const handle of directory.values()) {
    const relativePath = parentPath ? `${parentPath}/${handle.name}` : handle.name;

    if (handle.kind === 'directory') {
      await scanDirectory(handle, relativePath, files);
      continue;
    }

    const extension = getEncryptedMusicExtension(handle.name);

    if (extension) {
      files.push({
        name: handle.name,
        relativePath,
        extension,
        handle,
        parentHandle: directory
      });
    }
  }
}

function getEncryptedMusicExtension(filename: string): string | null {
  const lowerName = filename.toLowerCase();

  for (const extension of encryptedMusicExtensions) {
    if (lowerName.endsWith(extension)) {
      return extension;
    }
  }

  return null;
}

function openStorageDb(): Promise<IDBDatabase> {
  return new Promise((resolve, reject) => {
    const request = indexedDB.open(storageDbName, storageDbVersion);

    request.addEventListener('upgradeneeded', () => {
      request.result.createObjectStore(storageStoreName);
    });
    request.addEventListener('success', () => {
      resolve(request.result);
    });
    request.addEventListener('error', () => {
      reject(request.error);
    });
  });
}
