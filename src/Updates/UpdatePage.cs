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

    form {
      display: grid;
      gap: 18px;
      background: #ffffff;
      border: 1px solid #deded8;
      border-radius: 8px;
      padding: 22px;
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

    button:disabled {
      opacity: .55;
      cursor: not-allowed;
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
      form { background: #191c1b; border-color: #333835; }
      input { background: #111312; border-color: #444a47; }
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
        <input id="files" type="file" multiple webkitdirectory>
      </label>
      <div class="row">
        <button id="submit" type="submit">Upload</button>
      </div>
    </form>
    <output id="status">Ready.</output>
  </main>
  <script>
    const form = document.getElementById('update-form');
    const keyInput = document.getElementById('api-key');
    const fileInput = document.getElementById('files');
    const submit = document.getElementById('submit');
    const status = document.getElementById('status');

    const reverse = value => Array.from(value).reverse().join('');

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
          headers: { 'X-Update-Key': reverse(keyInput.value) },
          body
        });

        const text = await response.text();
        if (!response.ok) {
          status.textContent = `${response.status} ${response.statusText}\n${text}`;
          return;
        }

        const result = JSON.parse(text);
        status.textContent = JSON.stringify(result, null, 2);
      } catch (error) {
        status.textContent = error instanceof Error ? error.message : String(error);
      } finally {
        submit.disabled = false;
      }
    });
  </script>
</body>
</html>
""";
}
