namespace MusicDecrypto.Backend;

internal static class UpdatePage
{
    public const string Html = """
<!doctype html>
<html lang="zh-CN">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>MusicDecrypto Update</title>
  <style>
    :root {
      color-scheme: light dark;
      font-family: Inter, ui-sans-serif, system-ui, -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif;
      line-height: 1.45;
    }

    body {
      margin: 0;
      background: #f7f7f4;
      color: #202124;
    }

    main {
      max-width: 760px;
      margin: 0 auto;
      padding: 40px 20px;
    }

    h1 {
      margin: 0 0 24px;
      font-size: 28px;
      font-weight: 650;
    }

    form,
    section {
      display: grid;
      gap: 18px;
      background: #ffffff;
      border: 1px solid #deded8;
      border-radius: 8px;
      padding: 22px;
      margin-bottom: 18px;
    }

    label {
      display: grid;
      gap: 8px;
      font-size: 14px;
      font-weight: 600;
    }

    input {
      width: 100%;
      box-sizing: border-box;
      border: 1px solid #c9c9c2;
      border-radius: 6px;
      padding: 10px 12px;
      font: inherit;
      background: #fff;
      color: inherit;
    }

    .row {
      display: flex;
      gap: 10px;
      flex-wrap: wrap;
      align-items: center;
    }

    button {
      border: 0;
      border-radius: 6px;
      padding: 10px 14px;
      font: inherit;
      font-weight: 650;
      cursor: pointer;
      background: #176b5b;
      color: white;
    }

    button.secondary {
      background: #38414a;
    }

    button.danger {
      background: #9b2f2f;
    }

    button:disabled {
      opacity: .55;
      cursor: not-allowed;
    }

    .batch {
      display: grid;
      gap: 10px;
      border-top: 1px solid #deded8;
      padding-top: 14px;
    }

    .batch:first-child {
      border-top: 0;
      padding-top: 0;
    }

    .meta {
      color: #62665f;
      font-size: 13px;
      word-break: break-word;
    }

    .commits {
      margin: 0;
      padding-left: 20px;
      color: #202124;
      font-size: 13px;
    }

    output {
      display: block;
      margin-top: 18px;
      font-size: 13px;
      background: #1f2328;
      color: #f4f4f4;
      border-radius: 8px;
      padding: 14px;
      min-height: 44px;
    }

    output strong {
      display: block;
      font-size: 14px;
      margin-bottom: 6px;
    }

    output ul {
      margin: 8px 0 0;
      padding-left: 20px;
    }

    output code {
      font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
      word-break: break-word;
    }

    @media (prefers-color-scheme: dark) {
      body { background: #111312; color: #eeeeea; }
      form, section { background: #191c1b; border-color: #333835; }
      input { background: #111312; border-color: #444a47; }
      .batch { border-color: #333835; }
      .meta { color: #a5aaa4; }
      .commits { color: #eeeeea; }
    }
  </style>
</head>
<body>
  <main>
    <h1>MusicDecrypto Update</h1>
    <form id="update-form">
      <label>
        API Key
        <input id="api-key" type="password" autocomplete="current-password" required>
      </label>
      <label>
        ZIP Package
        <input id="files" type="file" accept=".zip,application/zip,application/x-zip-compressed">
      </label>
      <div class="row">
        <button id="submit" type="submit">Upload</button>
        <button id="refresh" class="secondary" type="button">Refresh Batches</button>
      </div>
    </form>
    <section>
      <div id="batches" class="meta">No batches loaded.</div>
    </section>
    <output id="status">Ready.</output>
  </main>
  <script>
    const form = document.getElementById('update-form');
    const keyInput = document.getElementById('api-key');
    const fileInput = document.getElementById('files');
    const submit = document.getElementById('submit');
    const refresh = document.getElementById('refresh');
    const batches = document.getElementById('batches');
    const status = document.getElementById('status');

    const sanitizeInput = value => String(value).replace(/[\p{Cc}\p{Cf}\p{Cs}\p{Co}\p{Cn}]/gu, '');
    const reverse = value => Array.from(sanitizeInput(value).trim()).reverse().join('');
    const authHeaders = () => ({ 'X-Update-Key': reverse(keyInput.value) });

    keyInput.addEventListener('input', () => {
      const sanitized = sanitizeInput(keyInput.value);
      if (sanitized !== keyInput.value) keyInput.value = sanitized;
    });

    async function request(path, options = {}) {
      const response = await fetch(path, {
        ...options,
        headers: {
          ...authHeaders(),
          ...(options.headers || {})
        }
      });
      const text = await response.text();
      if (!response.ok) {
        throw new Error(`${response.status} ${response.statusText}\n${text}`);
      }
      return text ? JSON.parse(text) : null;
    }

    function formatBytes(value) {
      if (value < 1024) return `${value} B`;
      if (value < 1024 * 1024) return `${(value / 1024).toFixed(1)} KB`;
      return `${(value / 1024 / 1024).toFixed(1)} MB`;
    }

    function escapeHtml(value) {
      return String(value)
        .replaceAll('&', '&amp;')
        .replaceAll('<', '&lt;')
        .replaceAll('>', '&gt;')
        .replaceAll('"', '&quot;')
        .replaceAll("'", '&#39;');
    }

    function formatSource(source) {
      if (!source || source.type !== 'git') return '';
      const commits = source.commits || [];
      const commitItems = commits.map(commit => {
        const target = commit.target ? `[${commit.target}] ` : '';
        const title = `${target}${commit.shortHash || ''} ${commit.subject || ''}`.trim();
        const body = commit.body ? `<div class="meta">${escapeHtml(commit.body)}</div>` : '';
        return `<li><strong>${escapeHtml(title)}</strong>${body}</li>`;
      }).join('');
      const repositories = (source.repositories || [])
        .map(repository => `${repository.target || 'backend'}: ${repository.range || ''}`)
        .join(', ');
      return `
        <div class="meta">git: ${escapeHtml(source.range || '')}</div>
        ${repositories ? `<div class="meta">repos: ${escapeHtml(repositories)}</div>` : ''}
        ${commitItems ? `<ul class="commits">${commitItems}</ul>` : ''}
      `;
    }

    function setStatus(html) {
      status.innerHTML = html;
    }

    function setStatusText(text) {
      status.textContent = text;
    }

    function renderFileList(files) {
      if (!files?.length) return '';
      return `<ul>${files.map(file => `
        <li><code>[${escapeHtml(file.target || 'backend')}] ${escapeHtml(file.path || '')}</code> · ${formatBytes(file.size || 0)}</li>
      `).join('')}</ul>`;
    }

    function renderUploadResult(result) {
      return `
        <strong>Upload complete</strong>
        <div>Batch: <code>${escapeHtml(result.batchId || '')}</code></div>
        <div>Directory: <code>${escapeHtml(result.directory || '')}</code></div>
        ${renderFileList(result.files || [])}
      `;
    }

    function renderApplyResult(result) {
      const deployment = result.deployment;
      const deploymentText = deployment
        ? `<div>Deployment: ${escapeHtml(deployment.status)}${deployment.logPath ? ` · log: <code>${escapeHtml(deployment.logPath)}</code>` : ''}</div>`
        : '';
      const applyRoots = Object.entries(result.applyRoots || {})
        .map(([target, path]) => `${target}: ${path}`)
        .join(', ');
      return `
        <strong>Update applied</strong>
        <div>Batch: <code>${escapeHtml(result.batchId || '')}</code></div>
        <div>Targets: <code>${escapeHtml((result.targets || []).join(', ') || 'backend')}</code></div>
        <div>Apply roots: <code>${escapeHtml(applyRoots)}</code></div>
        <div>${(result.files || []).length} files replaced.</div>
        ${deploymentText}
        ${renderFileList(result.files || [])}
      `;
    }

    function renderDeleteResult(result) {
      return `
        <strong>${result.deleted ? 'Batch deleted' : 'Batch was not found'}</strong>
        <div>Batch: <code>${escapeHtml(result.batchId || '')}</code></div>
      `;
    }

    async function loadBatches() {
      batches.textContent = 'Loading...';
      try {
        const items = await request('/update/batches');
        if (!items.length) {
          batches.textContent = 'No update batches.';
          return;
        }

        batches.innerHTML = '';
        for (const item of items) {
          const element = document.createElement('div');
          element.className = 'batch';
          element.innerHTML = `
            <strong>${item.batchId}</strong>
            <div class="meta">${item.fileCount} files · ${formatBytes(item.totalBytes)} · manifest: ${item.hasManifest ? 'yes' : 'no'}</div>
            <div class="meta">${item.directory}</div>
            ${formatSource(item.source)}
            <div class="row">
              <button type="button" data-action="apply" data-id="${item.batchId}">Apply Update</button>
              <button type="button" class="danger" data-action="delete" data-id="${item.batchId}">Delete</button>
            </div>
          `;
          batches.appendChild(element);
        }
      } catch (error) {
        batches.textContent = error instanceof Error ? error.message : String(error);
      }
    }

    form.addEventListener('submit', async event => {
      event.preventDefault();

      if (!fileInput.files.length) {
        setStatusText('Choose a zip package.');
        return;
      }

      submit.disabled = true;
      setStatusText('Uploading...');

      const body = new FormData();
      for (const file of fileInput.files) {
        body.append('files', file, file.webkitRelativePath || file.name);
      }

      try {
        const response = await fetch('/update', {
          method: 'POST',
          headers: authHeaders(),
          body
        });

        const text = await response.text();
        if (!response.ok) {
          setStatusText(`${response.status} ${response.statusText}\n${text}`);
          return;
        }

        const result = JSON.parse(text);
        setStatus(renderUploadResult(result));
        await loadBatches();
      } catch (error) {
        setStatusText(error instanceof Error ? error.message : String(error));
      } finally {
        submit.disabled = false;
      }
    });

    refresh.addEventListener('click', loadBatches);

    batches.addEventListener('click', async event => {
      const button = event.target instanceof HTMLButtonElement ? event.target : null;
      if (!button) return;

      const id = button.dataset.id;
      const action = button.dataset.action;
      if (!id || !action) return;

      button.disabled = true;
      try {
        if (action === 'apply') {
          const result = await request(`/update/${encodeURIComponent(id)}/apply`, { method: 'POST' });
          setStatus(renderApplyResult(result));
          if (result.deployment?.scheduled) {
            return;
          }
        } else if (action === 'delete') {
          const result = await request(`/update/${encodeURIComponent(id)}`, { method: 'DELETE' });
          setStatus(renderDeleteResult(result));
        }
        await loadBatches();
      } catch (error) {
        setStatusText(error instanceof Error ? error.message : String(error));
      } finally {
        button.disabled = false;
      }
    });
  </script>
</body>
</html>
""";
}
