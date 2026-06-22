import {
  type ChangeEvent,
  type DragEvent,
  useCallback,
  useEffect,
  useMemo,
  useRef,
  useState
} from 'react';
import { uploadFiles } from './lib/uploader';
import {
  deleteJob,
  downloadJob,
  fetchHealth,
  fetchJobDownload,
  fetchJobs,
  normalizeApiBaseUrl,
  sanitizeTextInput,
  type JobDownloadProgress
} from './lib/api';
import {
  authorizeStorageDirectory,
  deleteScannedStorageFiles,
  getStorageDirectoryStatus,
  scanStorageDirectoryFiles,
  saveBlobToStorageDirectory,
  type ScannedStorageFile,
  type StorageDirectoryKind,
  type StorageDirectoryStatus
} from './lib/file-system';
import type { BackendHealth, JobRecord, UploadProgress } from './types/backend';

const apiKeyStorageKey = 'musicdecrypto.apiKey';
const apiBaseUrlStorageKey = 'musicdecrypto.apiBaseUrl';
const pwaInstallDismissedStorageKey = 'musicdecrypto.pwaInstallDismissed';
const jobsPollIntervalMs = 3000;
const initialStorageDirectoryStatus: StorageDirectoryStatus = {
  supported: true,
  name: null,
  permission: null
};

type ConfirmDialogTone = 'default' | 'danger';

type ConfirmDialogRequest = {
  title: string;
  message: string;
  confirmText: string;
  cancelText: string;
  tone?: ConfirmDialogTone;
};

type AutoDownloadStatus = 'downloading' | 'saving' | 'completed' | 'failed' | 'skipped';

type AutoDownloadTask = {
  id: string;
  name: string;
  status: AutoDownloadStatus;
  bytesDownloaded: number;
  bytesTotal: number | null;
  percentage: number | null;
  message: string | null;
};

type BeforeInstallPromptEvent = Event & {
  prompt: () => Promise<void>;
  userChoice: Promise<{
    outcome: 'accepted' | 'dismissed';
    platform: string;
  }>;
};

type NavigatorWithStandalone = Navigator & {
  standalone?: boolean;
};

function readStoredApiKey(): string {
  try {
    const storedApiKey = window.localStorage.getItem(apiKeyStorageKey);
    return sanitizeTextInput(storedApiKey ?? import.meta.env.VITE_API_KEY ?? '').trim();
  } catch {
    return sanitizeTextInput(import.meta.env.VITE_API_KEY ?? '').trim();
  }
}

function readStoredApiBaseUrl(): string {
  try {
    const storedApiBaseUrl = window.localStorage.getItem(apiBaseUrlStorageKey);
    return sanitizeTextInput(storedApiBaseUrl ?? import.meta.env.VITE_API_BASE_URL ?? '');
  } catch {
    return sanitizeTextInput(import.meta.env.VITE_API_BASE_URL ?? '');
  }
}

function readPwaInstallDismissed(): boolean {
  try {
    return window.localStorage.getItem(pwaInstallDismissedStorageKey) === 'true';
  } catch {
    return false;
  }
}

function isRunningAsPwa(): boolean {
  return (
    window.matchMedia('(display-mode: standalone)').matches ||
    (window.navigator as NavigatorWithStandalone).standalone === true
  );
}

function App() {
  const [apiKey, setApiKey] = useState(readStoredApiKey);
  const [apiBaseUrl, setApiBaseUrl] = useState(readStoredApiBaseUrl);
  const [health, setHealth] = useState<BackendHealth | null>(null);
  const [jobs, setJobs] = useState<JobRecord[]>([]);
  const [progress, setProgress] = useState<UploadProgress[]>([]);
  const [sourceStorageDirectory, setSourceStorageDirectory] = useState<StorageDirectoryStatus>(
    initialStorageDirectoryStatus
  );
  const [destinationStorageDirectory, setDestinationStorageDirectory] = useState<StorageDirectoryStatus>(
    initialStorageDirectoryStatus
  );
  const [storageFiles, setStorageFiles] = useState<ScannedStorageFile[]>([]);
  const [jobsLoaded, setJobsLoaded] = useState(false);
  const [isUploading, setIsUploading] = useState(false);
  const [isDraggingFiles, setIsDraggingFiles] = useState(false);
  const [isScanningStorage, setIsScanningStorage] = useState(false);
  const [selectingStorageKind, setSelectingStorageKind] = useState<StorageDirectoryKind | null>(null);
  const [settingsOpen, setSettingsOpen] = useState(false);
  const [downloadProgressOpen, setDownloadProgressOpen] = useState(false);
  const [autoDownloadTasks, setAutoDownloadTasks] = useState<AutoDownloadTask[]>([]);
  const [confirmDialog, setConfirmDialog] = useState<ConfirmDialogRequest | null>(null);
  const [installPromptEvent, setInstallPromptEvent] = useState<BeforeInstallPromptEvent | null>(null);
  const [showPwaInstallPrompt, setShowPwaInstallPrompt] = useState(
    () => !isRunningAsPwa() && !readPwaInstallDismissed()
  );
  const [error, setError] = useState<string | null>(null);
  const dragDepth = useRef(0);
  const initializedJobSnapshot = useRef(false);
  const autoDownloadHandledJobIds = useRef(new Set<string>());
  const autoDownloadingJobIds = useRef(new Set<string>());
  const sourceFallbackAllowed = useRef<boolean | null>(null);
  const sourceFallbackPrompt = useRef<Promise<boolean> | null>(null);
  const confirmResolver = useRef<((confirmed: boolean) => void) | null>(null);
  const settingsMenuRef = useRef<HTMLDivElement>(null);
  const downloadProgressMenuRef = useRef<HTMLDivElement>(null);

  const auth = useMemo(
    () => ({
      apiKey: apiKey.trim(),
      apiBaseUrl: normalizeApiBaseUrl(apiBaseUrl)
    }),
    [apiBaseUrl, apiKey]
  );

  const refreshJobs = useCallback(async () => {
    const nextJobs = await fetchJobs(auth);
    setJobs(nextJobs);
    setJobsLoaded(true);
  }, [auth]);

  const scanAuthorizedStorage = useCallback(async () => {
    setIsScanningStorage(true);

    try {
      const files = await scanStorageDirectoryFiles();
      setStorageFiles(files);
    } catch (err) {
      setStorageFiles([]);
      setError(err instanceof Error ? err.message : '本地存储扫描失败');
    } finally {
      setIsScanningStorage(false);
    }
  }, []);

  const closeConfirmDialog = useCallback((confirmed: boolean) => {
    confirmResolver.current?.(confirmed);
    confirmResolver.current = null;
    setConfirmDialog(null);
  }, []);

  const requestConfirm = useCallback((request: ConfirmDialogRequest) => {
    confirmResolver.current?.(false);

    return new Promise<boolean>((resolve) => {
      confirmResolver.current = resolve;
      setConfirmDialog(request);
    });
  }, []);

  const updateAutoDownloadTask = useCallback(
    (job: JobRecord, patch: Partial<Omit<AutoDownloadTask, 'id' | 'name'>>) => {
      setAutoDownloadTasks((tasks) => {
        const nextTask: AutoDownloadTask = {
          id: job.id,
          name: job.originalFileName,
          status: 'downloading',
          bytesDownloaded: 0,
          bytesTotal: null,
          percentage: null,
          message: null,
          ...patch
        };
        const existingTaskIndex = tasks.findIndex((task) => task.id === job.id);

        if (existingTaskIndex === -1) {
          return [nextTask, ...tasks];
        }

        return tasks.map((task, index) =>
          index === existingTaskIndex ? { ...task, name: job.originalFileName, ...patch } : task
        );
      });
    },
    []
  );

  const autoDownloadCompletedJob = useCallback(
    async (job: JobRecord) => {
      updateAutoDownloadTask(job, { status: 'downloading' });

      try {
        const download = await fetchJobDownload(
          job.id,
          auth,
          (downloadProgress: JobDownloadProgress) => {
            updateAutoDownloadTask(job, {
              status: 'downloading',
              bytesDownloaded: downloadProgress.bytesDownloaded,
              bytesTotal: downloadProgress.bytesTotal,
              percentage: downloadProgress.percentage
            });
          }
        );
        const targetKind = await getAutoDownloadTargetKind(
          sourceStorageDirectory,
          destinationStorageDirectory,
          sourceFallbackAllowed,
          sourceFallbackPrompt,
          requestConfirm
        );

        if (!targetKind) {
          updateAutoDownloadTask(job, {
            status: 'skipped',
            percentage: 100,
            message: '已跳过自动保存'
          });
          autoDownloadHandledJobIds.current.add(job.id);
          return;
        }

        updateAutoDownloadTask(job, {
          status: 'saving',
          bytesDownloaded: download.blob.size,
          bytesTotal: download.blob.size,
          percentage: 100
        });
        const savedFilename = await saveBlobToStorageDirectory(targetKind, download.blob, download.filename);
        updateAutoDownloadTask(job, {
          status: 'completed',
          bytesDownloaded: download.blob.size,
          bytesTotal: download.blob.size,
          percentage: 100,
          message: `已保存为 ${savedFilename}`
        });
        autoDownloadHandledJobIds.current.add(job.id);
      } catch (err) {
        updateAutoDownloadTask(job, {
          status: 'failed',
          message: err instanceof Error ? err.message : '自动下载失败'
        });
        autoDownloadHandledJobIds.current.add(job.id);
        setError(err instanceof Error ? err.message : '自动下载失败');
      } finally {
        autoDownloadingJobIds.current.delete(job.id);
      }
    },
    [
      auth,
      destinationStorageDirectory,
      requestConfirm,
      sourceStorageDirectory,
      updateAutoDownloadTask
    ]
  );

  useEffect(() => {
    let ignore = false;

    setJobsLoaded(false);
    initializedJobSnapshot.current = false;
    autoDownloadHandledJobIds.current = new Set<string>();
    autoDownloadingJobIds.current = new Set<string>();
    setAutoDownloadTasks([]);

    fetchHealth(auth)
      .then((result) => {
        if (!ignore) {
          setHealth(result);
        }
      })
      .catch(() => {
        if (!ignore) {
          setHealth(null);
        }
      });

    refreshJobs().catch(() => {
      if (!ignore) {
        setJobs([]);
      }
    });

    return () => {
      ignore = true;
    };
  }, [auth, refreshJobs]);

  useEffect(() => {
    if (isRunningAsPwa()) {
      setShowPwaInstallPrompt(false);
      return;
    }

    const displayModeQuery = window.matchMedia('(display-mode: standalone)');

    const handleBeforeInstallPrompt = (event: Event) => {
      event.preventDefault();
      setInstallPromptEvent(event as BeforeInstallPromptEvent);

      if (!readPwaInstallDismissed()) {
        setShowPwaInstallPrompt(true);
      }
    };

    const handleAppInstalled = () => {
      setInstallPromptEvent(null);
      setShowPwaInstallPrompt(false);
    };

    const handleDisplayModeChange = () => {
      if (isRunningAsPwa()) {
        setShowPwaInstallPrompt(false);
      }
    };

    window.addEventListener('beforeinstallprompt', handleBeforeInstallPrompt);
    window.addEventListener('appinstalled', handleAppInstalled);
    displayModeQuery.addEventListener('change', handleDisplayModeChange);

    return () => {
      window.removeEventListener('beforeinstallprompt', handleBeforeInstallPrompt);
      window.removeEventListener('appinstalled', handleAppInstalled);
      displayModeQuery.removeEventListener('change', handleDisplayModeChange);
    };
  }, []);

  useEffect(() => {
    const hasActiveJobs = jobs.some((job) => job.status === 'Queued' || job.status === 'Running');

    if (!hasActiveJobs) {
      return;
    }

    const intervalId = window.setInterval(() => {
      refreshJobs().catch(() => {
        setJobs([]);
      });
    }, jobsPollIntervalMs);

    return () => {
      window.clearInterval(intervalId);
    };
  }, [jobs, refreshJobs]);

  useEffect(() => {
    if (!jobsLoaded) {
      return;
    }

    const completedJobs = jobs.filter((job) => job.status === 'Completed');

    if (!initializedJobSnapshot.current) {
      completedJobs.forEach((job) => autoDownloadHandledJobIds.current.add(job.id));
      initializedJobSnapshot.current = true;
      return;
    }

    completedJobs.forEach((job) => {
      if (
        autoDownloadHandledJobIds.current.has(job.id) ||
        autoDownloadingJobIds.current.has(job.id)
      ) {
        return;
      }

      autoDownloadingJobIds.current.add(job.id);
      void autoDownloadCompletedJob(job);
    });
  }, [autoDownloadCompletedJob, jobs, jobsLoaded]);

  useEffect(() => {
    let ignore = false;

    Promise.all([
      getStorageDirectoryStatus('source'),
      getStorageDirectoryStatus('destination')
    ])
      .then(([sourceStatus, destinationStatus]) => {
        if (!ignore) {
          setSourceStorageDirectory(sourceStatus);
          setDestinationStorageDirectory(destinationStatus);

          if (sourceStatus.permission === 'granted') {
            void scanAuthorizedStorage();
          }
        }
      })
      .catch(() => {
        if (!ignore) {
          const unsupportedStatus = {
            supported: false,
            name: null,
            permission: null
          };
          setSourceStorageDirectory(unsupportedStatus);
          setDestinationStorageDirectory(unsupportedStatus);
        }
      });

    return () => {
      ignore = true;
    };
  }, [scanAuthorizedStorage]);

  useEffect(() => {
    try {
      window.localStorage.setItem(apiKeyStorageKey, apiKey);
    } catch {
      // Ignore storage failures so private browsing or blocked storage does not break uploads.
    }
  }, [apiKey]);

  useEffect(() => {
    try {
      window.localStorage.setItem(apiBaseUrlStorageKey, apiBaseUrl);
    } catch {
      // Ignore storage failures so private browsing or blocked storage does not break requests.
    }
  }, [apiBaseUrl]);

  useEffect(() => {
    if (!settingsOpen && !downloadProgressOpen) {
      return;
    }

    const handlePointerDown = (event: PointerEvent) => {
      if (settingsMenuRef.current?.contains(event.target as Node)) {
        return;
      }

      if (downloadProgressMenuRef.current?.contains(event.target as Node)) {
        return;
      }

      setSettingsOpen(false);
      setDownloadProgressOpen(false);
    };

    document.addEventListener('pointerdown', handlePointerDown);

    return () => {
      document.removeEventListener('pointerdown', handlePointerDown);
    };
  }, [downloadProgressOpen, settingsOpen]);

  useEffect(() => {
    if (!confirmDialog) {
      return;
    }

    const handleKeyDown = (event: KeyboardEvent) => {
      if (event.key === 'Escape') {
        closeConfirmDialog(false);
      }
    };

    document.addEventListener('keydown', handleKeyDown);

    return () => {
      document.removeEventListener('keydown', handleKeyDown);
    };
  }, [closeConfirmDialog, confirmDialog]);

  const handleFilesSelected = async (selectedFiles: FileList | File[] | null) => {
    const files = Array.from(selectedFiles ?? []);

    if (files.length === 0) {
      return;
    }

    setError(null);
    setProgress([]);
    setIsUploading(true);

    try {
      await uploadFiles(Array.from(files), auth, setProgress);
      await refreshJobs();
    } catch (err) {
      setError(err instanceof Error ? err.message : '上传失败');
    } finally {
      setIsUploading(false);
    }
  };

  const handleFileInputChange = (event: ChangeEvent<HTMLInputElement>) => {
    void handleFilesSelected(event.target.files);
    event.currentTarget.value = '';
  };

  const handleInstallPwa = async () => {
    if (!installPromptEvent) {
      return;
    }

    await installPromptEvent.prompt();
    const choice = await installPromptEvent.userChoice;

    if (choice.outcome === 'accepted') {
      setShowPwaInstallPrompt(false);
    }

    setInstallPromptEvent(null);
  };

  const handleDismissPwaInstall = () => {
    try {
      window.localStorage.setItem(pwaInstallDismissedStorageKey, 'true');
    } catch {
      // Ignore storage failures; the prompt can still be dismissed for this session.
    }

    setShowPwaInstallPrompt(false);
  };

  const handleStorageFilesUnlock = async (files: ScannedStorageFile[]) => {
    if (files.length === 0 || isUploading) {
      return;
    }

    setError(null);
    setIsUploading(true);

    try {
      const uploadItems = await Promise.all(files.map((file) => file.handle.getFile()));
      await uploadFiles(uploadItems, auth, setProgress);
      await refreshJobs();

      const shouldDelete = await requestConfirm({
        title: '删除未解锁原文件',
        message:
          files.length === 1
            ? `已提交 ${files[0].relativePath} 的解锁任务。确认解锁完成后，是否删除这个未解锁的原文件？`
            : `已提交 ${files.length} 个文件的解锁任务。确认全部解锁完成后，是否删除这些未解锁的原文件？`,
        confirmText: '删除原文件',
        cancelText: '保留原文件',
        tone: 'danger'
      });

      if (shouldDelete) {
        await deleteScannedStorageFiles(files);
        await scanAuthorizedStorage();
      }
    } catch (err) {
      setError(err instanceof Error ? err.message : '解锁失败');
    } finally {
      setIsUploading(false);
    }
  };

  const handleStorageFileDelete = async (file: ScannedStorageFile) => {
    const shouldDelete = await requestConfirm({
      title: '删除未解锁原文件',
      message: `确认删除 ${file.relativePath}？此操作会删除本地来源目录中的文件。`,
      confirmText: '删除',
      cancelText: '取消',
      tone: 'danger'
    });

    if (!shouldDelete) {
      return;
    }

    setError(null);

    try {
      await deleteScannedStorageFiles([file]);
      await scanAuthorizedStorage();
    } catch (err) {
      setError(err instanceof Error ? err.message : '删除失败');
    }
  };

  const handleAuthorizeStorage = async (kind: StorageDirectoryKind) => {
    setError(null);
    setSelectingStorageKind(kind);

    try {
      const status = await authorizeStorageDirectory(kind);

      if (kind === 'source') {
        setSourceStorageDirectory(status);
      } else {
        setDestinationStorageDirectory(status);
      }

      if (kind === 'source' && status.permission === 'granted') {
        await scanAuthorizedStorage();
      } else if (kind === 'source') {
        setStorageFiles([]);
      }
    } catch (err) {
      if (err instanceof DOMException && err.name === 'AbortError') {
        return;
      }

      setError(err instanceof Error ? err.message : '存储授权失败');
    } finally {
      setSelectingStorageKind(null);
    }
  };

  const isFileDrag = (event: DragEvent<HTMLElement>) =>
    Array.from(event.dataTransfer.types).includes('Files');

  const handleDragEnter = (event: DragEvent<HTMLElement>) => {
    if (!isFileDrag(event)) {
      return;
    }

    event.preventDefault();
    dragDepth.current += 1;
    setIsDraggingFiles(true);
  };

  const handleDragOver = (event: DragEvent<HTMLElement>) => {
    if (!isFileDrag(event)) {
      return;
    }

    event.preventDefault();
    event.dataTransfer.dropEffect = 'copy';
  };

  const handleDragLeave = (event: DragEvent<HTMLElement>) => {
    if (!isFileDrag(event)) {
      return;
    }

    event.preventDefault();
    dragDepth.current = Math.max(0, dragDepth.current - 1);

    if (dragDepth.current === 0) {
      setIsDraggingFiles(false);
    }
  };

  const handleDrop = (event: DragEvent<HTMLElement>) => {
    if (!isFileDrag(event)) {
      return;
    }

    event.preventDefault();
    dragDepth.current = 0;
    setIsDraggingFiles(false);
    void handleFilesSelected(event.dataTransfer.files);
  };

  const handleDeleteJob = async (job: JobRecord) => {
    setError(null);

    try {
      await deleteJob(job.id, auth);
      await refreshJobs();
    } catch (err) {
      setError(err instanceof Error ? err.message : '删除失败');
    }
  };

  return (
    <main
      className={`app-shell${isDraggingFiles ? ' app-shell-dragging' : ''}`}
      onDragEnter={handleDragEnter}
      onDragLeave={handleDragLeave}
      onDragOver={handleDragOver}
      onDrop={handleDrop}
    >
      <section className="workspace">
        <header className="topbar">
          <div>
            <p className="eyebrow">MusicDecrypto</p>
            <h1>音乐解锁</h1>
          </div>
          <div className="topbar-actions">
            <span className={health ? 'status status-ok' : 'status status-offline'}>
              {health ? `Backend ${health.status}` : 'Backend offline'}
            </span>
            <div className="download-progress-menu" ref={downloadProgressMenuRef}>
              <button
                aria-expanded={downloadProgressOpen}
                aria-haspopup="true"
                onClick={() => {
                  setSettingsOpen(false);
                  setDownloadProgressOpen((open) => !open);
                }}
                type="button"
              >
                下载进度
              </button>
              {downloadProgressOpen ? (
                <div className="download-progress-panel">
                  <div className="download-progress-heading">
                    <strong>自动下载</strong>
                    <span>{getActiveAutoDownloadCount(autoDownloadTasks)} 个进行中</span>
                  </div>
                  {autoDownloadTasks.length > 0 ? (
                    <div className="download-task-list">
                      {autoDownloadTasks.map((task) => (
                        <div className="download-task" key={task.id}>
                          <div>
                            <strong>{task.name}</strong>
                            <span>{getAutoDownloadStatusText(task)}</span>
                          </div>
                          <progress
                            max="100"
                            value={
                              task.percentage ??
                              (task.status === 'completed' || task.status === 'skipped' ? 100 : 0)
                            }
                          />
                          <small>{getAutoDownloadDetailText(task)}</small>
                        </div>
                      ))}
                    </div>
                  ) : (
                    <p className="empty">暂无自动下载任务</p>
                  )}
                </div>
              ) : null}
            </div>
            <div className="settings-menu" ref={settingsMenuRef}>
              <button
                aria-expanded={settingsOpen}
                aria-haspopup="true"
                onClick={() => {
                  setDownloadProgressOpen(false);
                  setSettingsOpen((open) => !open);
                }}
                type="button"
              >
                设置
              </button>
              {settingsOpen ? (
                <div className="settings-panel">
                  <label className="field">
                    <span>后端地址</span>
                    <input
                      value={apiBaseUrl}
                      onChange={(event) => setApiBaseUrl(sanitizeTextInput(event.target.value))}
                      placeholder="例如 api.example.com 或 127.0.0.1:5000"
                      type="text"
                    />
                  </label>
                  <label className="field">
                    <span>API Key</span>
                    <input
                      value={apiKey}
                      onChange={(event) => setApiKey(sanitizeTextInput(event.target.value).trim())}
                      placeholder="未配置后端密钥时可留空"
                      type="password"
                    />
                  </label>
                </div>
              ) : null}
            </div>
          </div>
        </header>

        {showPwaInstallPrompt ? (
          <section className="install-banner" aria-label="安装应用">
            <div>
              <strong>安装 MusicDecrypto</strong>
              <span>以独立应用窗口运行，减少浏览器界面对本地存储流程的干扰。</span>
            </div>
            <div className="install-actions">
              {installPromptEvent ? (
                <button className="primary-button" onClick={() => void handleInstallPwa()} type="button">
                  安装应用
                </button>
              ) : null}
              <button onClick={handleDismissPwaInstall} type="button">
                跳过
              </button>
            </div>
          </section>
        ) : null}

        <div className="panel">
          <section className="storage-panel" aria-labelledby="storage-heading">
            <div className="storage-heading">
              <h2 id="storage-heading">本地存储</h2>
              <span
                className={getStorageBadgeClassName(
                  sourceStorageDirectory,
                  destinationStorageDirectory
                )}
              >
                {getStorageBadgeText(sourceStorageDirectory, destinationStorageDirectory)}
              </span>
            </div>
            <div className="storage-grid">
              <button
                className="storage-device storage-device-current"
                disabled={!sourceStorageDirectory.supported || selectingStorageKind !== null}
                onClick={() => void handleAuthorizeStorage('source')}
                type="button"
              >
                <span className="storage-icon" aria-hidden="true" />
                <small>来源目录</small>
                <strong>{sourceStorageDirectory.name ?? '添加存储'}</strong>
                <span>
                  {getStorageDetailText(
                    sourceStorageDirectory,
                    selectingStorageKind === 'source',
                    '可扫描待解锁文件'
                  )}
                </span>
              </button>
              <span className="storage-flow-arrow" aria-hidden="true" />
              <button
                className="storage-device storage-device-current"
                disabled={!destinationStorageDirectory.supported || selectingStorageKind !== null}
                onClick={() => void handleAuthorizeStorage('destination')}
                type="button"
              >
                <span className="storage-icon storage-icon-destination" aria-hidden="true" />
                <small>保存目录</small>
                <strong>{destinationStorageDirectory.name ?? '添加存储'}</strong>
                <span>
                  {getStorageDetailText(
                    destinationStorageDirectory,
                    selectingStorageKind === 'destination',
                    '用于保存解锁产物'
                  )}
                </span>
              </button>
            </div>
            <div className="storage-file-panel">
              <div className="storage-file-heading">
                <strong>可处理文件</strong>
                <div>
                  <span>
                    {getStorageFileSummary(sourceStorageDirectory, storageFiles, isScanningStorage)}
                  </span>
                  {storageFiles.length > 0 ? (
                    <button
                      className="storage-unlock-all"
                      disabled={isUploading || isScanningStorage}
                      onClick={() => void handleStorageFilesUnlock(storageFiles)}
                      type="button"
                    >
                      全部解锁
                    </button>
                  ) : null}
                </div>
              </div>
              {storageFiles.length > 0 ? (
                <div className="storage-file-list">
                  {storageFiles.map((file) => (
                    <div className="storage-file-row" key={file.relativePath}>
                      <span>{file.relativePath}</span>
                      <strong>{file.extension}</strong>
                      <button
                        disabled={isUploading}
                        onClick={() => void handleStorageFilesUnlock([file])}
                        type="button"
                      >
                        解锁
                      </button>
                      <button
                        className="storage-file-delete"
                        disabled={isUploading}
                        onClick={() => void handleStorageFileDelete(file)}
                        type="button"
                      >
                        删除
                      </button>
                    </div>
                  ))}
                </div>
              ) : null}
            </div>
          </section>

          <label className="upload-zone">
            <input multiple onChange={handleFileInputChange} type="file" />
            <span>{isUploading ? '上传中...' : '选择文件或拖到此页面上传'}</span>
          </label>

          {error ? <p className="error">{error}</p> : null}

          <div className="progress-list">
            {progress.map((item) => (
              <div className="progress-row" key={item.id}>
                <div>
                  <strong>{item.name}</strong>
                  <span>{Math.round(item.percentage)}%</span>
                </div>
                <progress max="100" value={item.percentage} />
              </div>
            ))}
          </div>
        </div>

        <section className="jobs">
          <div className="section-heading">
            <h2>Jobs</h2>
            <button onClick={() => void refreshJobs()} type="button">
              刷新
            </button>
          </div>

          <div className="job-list">
            {jobs.length === 0 ? (
              <p className="empty">暂无任务</p>
            ) : (
              jobs.map((job) => (
                <article className="job-item" key={job.id}>
                  <div>
                    <strong>{job.originalFileName}</strong>
                    <span>{job.status}</span>
                    {job.error ? <p className="job-error">{job.error}</p> : null}
                  </div>
                  <div className="job-actions">
                    {job.status === 'Completed' ? (
                      <button onClick={() => void downloadJob(job.id, auth)} type="button">
                        下载
                      </button>
                    ) : null}
                    <button
                      className="danger-button"
                      disabled={job.status === 'Running'}
                      onClick={() => void handleDeleteJob(job)}
                      type="button"
                    >
                      删除
                    </button>
                  </div>
                </article>
              ))
            )}
          </div>
        </section>
      </section>

      {confirmDialog ? (
        <div
          className="confirm-backdrop"
          onMouseDown={(event) => {
            if (event.target === event.currentTarget) {
              closeConfirmDialog(false);
            }
          }}
          role="presentation"
        >
          <section
            aria-modal="true"
            className="confirm-dialog"
            role="dialog"
            aria-labelledby="confirm-title"
          >
            <h2 id="confirm-title">{confirmDialog.title}</h2>
            <p>{confirmDialog.message}</p>
            <div className="confirm-actions">
              <button onClick={() => closeConfirmDialog(false)} type="button">
                {confirmDialog.cancelText}
              </button>
              <button
                className={confirmDialog.tone === 'danger' ? 'danger-button' : 'primary-button'}
                onClick={() => closeConfirmDialog(true)}
                type="button"
              >
                {confirmDialog.confirmText}
              </button>
            </div>
          </section>
        </div>
      ) : null}
    </main>
  );
}

function getStorageBadgeText(
  sourceStatus: StorageDirectoryStatus,
  destinationStatus: StorageDirectoryStatus
): string {
  if (!sourceStatus.supported || !destinationStatus.supported) {
    return '不支持';
  }

  if (sourceStatus.permission === 'granted' && destinationStatus.permission === 'granted') {
    return '两处已授权';
  }

  if (sourceStatus.permission === 'granted' || destinationStatus.permission === 'granted') {
    return '部分授权';
  }

  return '未授权';
}

function getStorageBadgeClassName(
  sourceStatus: StorageDirectoryStatus,
  destinationStatus: StorageDirectoryStatus
): string {
  if (!sourceStatus.supported || !destinationStatus.supported) {
    return 'storage-badge storage-badge-offline';
  }

  if (sourceStatus.permission === 'granted' && destinationStatus.permission === 'granted') {
    return 'storage-badge storage-badge-ok';
  }

  return 'storage-badge';
}

function getStorageDetailText(
  status: StorageDirectoryStatus,
  isSelectingStorage: boolean,
  emptyText: string
): string {
  if (isSelectingStorage) {
    return '授权中...';
  }

  if (!status.supported) {
    return '当前浏览器不可用';
  }

  if (status.permission === 'granted') {
    return '读写权限可用';
  }

  if (status.name) {
    return '等待重新授权';
  }

  return emptyText;
}

function getStorageFileSummary(
  status: StorageDirectoryStatus,
  files: ScannedStorageFile[],
  isScanningStorage: boolean
): string {
  if (isScanningStorage) {
    return '扫描中...';
  }

  if (status.permission !== 'granted') {
    return '授权后扫描';
  }

  return files.length > 0 ? `${files.length} 个文件` : '未发现匹配文件';
}

function getActiveAutoDownloadCount(tasks: AutoDownloadTask[]): number {
  return tasks.filter((task) => task.status === 'downloading' || task.status === 'saving').length;
}

function getAutoDownloadStatusText(task: AutoDownloadTask): string {
  switch (task.status) {
    case 'downloading':
      return '下载中';
    case 'saving':
      return '保存中';
    case 'completed':
      return '已完成';
    case 'failed':
      return '失败';
    case 'skipped':
      return '已跳过';
  }
}

function getAutoDownloadDetailText(task: AutoDownloadTask): string {
  if (task.message) {
    return task.message;
  }

  if (task.status === 'downloading') {
    const downloaded = formatBytes(task.bytesDownloaded);
    return task.bytesTotal
      ? `${downloaded} / ${formatBytes(task.bytesTotal)}`
      : `${downloaded} 已下载`;
  }

  return getAutoDownloadStatusText(task);
}

function formatBytes(value: number): string {
  if (value < 1024) {
    return `${value} B`;
  }

  const units = ['KB', 'MB', 'GB'];
  let nextValue = value / 1024;
  let unitIndex = 0;

  while (nextValue >= 1024 && unitIndex < units.length - 1) {
    nextValue /= 1024;
    unitIndex += 1;
  }

  return `${nextValue.toFixed(nextValue >= 10 ? 1 : 2)} ${units[unitIndex]}`;
}

async function getAutoDownloadTargetKind(
  sourceStatus: StorageDirectoryStatus,
  destinationStatus: StorageDirectoryStatus,
  sourceFallbackAllowed: { current: boolean | null },
  sourceFallbackPrompt: { current: Promise<boolean> | null },
  requestConfirm: (request: ConfirmDialogRequest) => Promise<boolean>
): Promise<StorageDirectoryKind | null> {
  if (destinationStatus.permission === 'granted') {
    return 'destination';
  }

  if (sourceStatus.permission !== 'granted') {
    throw new Error('自动下载失败：未授权保存目录或来源目录');
  }

  if (sourceFallbackAllowed.current === null) {
    sourceFallbackPrompt.current ??= requestConfirm({
      title: '选择自动保存位置',
      message: '尚未选择保存目录。是否确认不选择保存目录，并将解锁产物自动保存到来源目录？',
      confirmText: '保存到来源目录',
      cancelText: '跳过自动下载'
    }).finally(() => {
      sourceFallbackPrompt.current = null;
    });
    sourceFallbackAllowed.current = await sourceFallbackPrompt.current;
  }

  return sourceFallbackAllowed.current ? 'source' : null;
}

export default App;
