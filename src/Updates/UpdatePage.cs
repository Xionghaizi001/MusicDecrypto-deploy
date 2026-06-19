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

    output {
      display: block;
      margin-top: 18px;
      white-space: pre-wrap;
      font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
      font-size: 13px;
      background: #1f2328;
      color: #f4f4f4;
      border-radius: 8px;
      padding: 14px;
      min-height: 44px;
    }

    @media (prefers-color-scheme: dark) {
      body { background: #111312; color: #eeeeea; }
      form, section { background: #191c1b; border-color: #333835; }
      input { background: #111312; border-color: #444a47; }
      .batch { border-color: #333835; }
      .meta { color: #a5aaa4; }
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
        Files
        <input id="files" type="file" multiple>
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

    const reverse = value => Array.from(value).reverse().join('');
    const authHeaders = () => ({ 'X-Update-Key': reverse(keyInput.value) });

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
            <div class="row">
              <button type="button" data-action="apply" data-id="${item.batchId}">Apply</button>
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
        status.textContent = 'Choose at least one file.';
        return;
      }

      submit.disabled = true;
      status.textContent = 'Uploading...';

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
          status.textContent = `${response.status} ${response.statusText}\n${text}`;
          return;
        }

        const result = JSON.parse(text);
        status.textContent = JSON.stringify(result, null, 2);
        await loadBatches();
      } catch (error) {
        status.textContent = error instanceof Error ? error.message : String(error);
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
          status.textContent = JSON.stringify(result, null, 2);
        } else if (action === 'delete') {
          const result = await request(`/update/${encodeURIComponent(id)}`, { method: 'DELETE' });
          status.textContent = JSON.stringify(result, null, 2);
        }
        await loadBatches();
      } catch (error) {
        status.textContent = error instanceof Error ? error.message : String(error);
      } finally {
        button.disabled = false;
      }
    });
  </script>
</body>
</html>
""";
}
