# MusicDecrypto Backend

ASP.NET Core Minimal API backend for uploading encrypted music files with tus resumable uploads, then processing them through the packaged `musicdecrypto` command-line tool.

## Stack

- .NET 10 LTS, `net10.0`
- ASP.NET Core Minimal API
- `tusdotnet` for resumable uploads at `/files`
- Local filesystem storage for uploads, outputs, and job state
- `deploy/package/musicdecrypto-linux-x64.tar.gz` contains the packaged native CLI and is extracted during deployment

## API

- `GET /healthz`: health check, no API key required
- `POST /files`: tus upload creation endpoint
- `HEAD /files/{tus-id}`: tus upload offset check
- `PATCH /files/{tus-id}`: tus upload chunk endpoint
- `GET /api/jobs`: list jobs
- `GET /api/jobs/{id}`: get one job
- `GET /api/jobs/{id}/download`: download decrypted output
- `GET /update`: simple browser upload page for update files
- `POST /update`: upload update files into `UpdateRoot/{batchId}`
- `GET /update/batches`: list uploaded update batches
- `POST /update/{batchId}/apply`: verify and apply one update batch into `UpdateApplyRoot`
- `DELETE /update/{batchId}`: delete one uploaded update batch

If `MusicDecrypto:ApiKey` is set, calls under `/files` and `/api` must include either:

- `X-Api-Key: <key>`
- `Authorization: Bearer <key>`

The update endpoints use `X-Update-Key` with the configured API key reversed. For example, if the API key is `abc123`, send `X-Update-Key: 321cba`. The browser page accepts the normal API key and reverses it before sending the request.

## Local Development

Install the .NET 10 SDK, then run:

```bash
dotnet restore
dotnet run
```

The default local URL is `http://127.0.0.1:5080`.

The CLI package is stored compressed in `deploy/package/musicdecrypto-linux-x64.tar.gz`. If you need to process jobs locally, extract it first:

```bash
PACKAGE_DIR=package scripts/manage.sh extract-package
```

The extracted root `package/` directory is ignored by git. Keep source control on the compressed archive under `deploy/package/`.

For frontend development, local CORS is enabled for:

- `http://localhost:5173`
- `http://127.0.0.1:5173`

Upload a file with the included tus-compatible test script:

```bash
chmod +x scripts/tus-upload.sh
scripts/tus-upload.sh http://127.0.0.1:5080 ./sample.ncm
```

Development helper commands:

```bash
scripts/dev.sh server
scripts/dev.sh pack --git HEAD~1..HEAD -o /tmp/update.zip
scripts/dev.sh pack --files Program.cs src/Updates/UpdateEndpoints.cs -o /tmp/update.zip
```

`scripts/dev.sh server` uses the normally installed `dotnet` from `PATH`. If it is installed in a custom location, run it with `DOTNET_BIN=/path/to/dotnet`.

The update package format is a zip with this root structure:

```text
musicdecrypto-update.json
files/<relative target paths>
```

The manifest format is `musicdecrypto.update.v1` and lists each target path, package source path, file size, and SHA-256. The backend verifies the manifest before applying files.

## VPS Deployment

These commands assume Ubuntu/Debian. The default installation uses:

- app directory: this checked-out `backend` directory
- service user: the owner of this checked-out `backend` directory
- data directory: `/var/lib/musicdecrypto`
- temporary tus directory: `/var/tmp/musicdecrypto`
- update upload directory: `/var/lib/musicdecrypto/updates`
- update apply directory: the app directory
- service port: `127.0.0.1:5080`

This keeps the runtime directory and `/update` apply target aligned when installing from a cloned repository. If you prefer a traditional `/opt` install, set `APP_DIR=/opt/musicdecrypto/backend`; `PUBLISH_DIR`, `PACKAGE_DIR`, and `APPLY_DIR` will follow that app directory unless you override them explicitly.

Run an environment check:

```bash
scripts/manage.sh env-check
```

On a VPS with no .NET SDK installed:

```bash
sudo scripts/manage.sh install-deps
```

`install-deps` first tries the Microsoft apt feed package `dotnet-sdk-10.0`. If that package is not available for the current distro/feed, it falls back to Microsoft's official install script:

- install script: `https://dot.net/v1/dotnet-install.sh`
- default SDK feed used by that script: `https://builds.dotnet.microsoft.com/dotnet`
- default install directory used by this project: `/opt/dotnet`

Override the fallback settings if needed:

```bash
sudo \
  DOTNET_CHANNEL=10.0 \
  DOTNET_INSTALL_DIR=/opt/dotnet \
  DOTNET_INSTALL_SCRIPT_URL=https://dot.net/v1/dotnet-install.sh \
  scripts/manage.sh install-deps
```

Install and start the service:

```bash
sudo API_KEY='replace-with-a-long-random-secret' scripts/manage.sh install-service
```

Override settings with environment variables when installing:

```bash
sudo \
  APP_DIR=/opt/musicdecrypto/backend \
  PORT=5081 \
  DATA_DIR=/srv/musicdecrypto/data \
  TEMP_DIR=/srv/musicdecrypto/tmp \
  UPDATE_DIR=/srv/musicdecrypto/updates \
  ALLOWED_ORIGINS=https://your-frontend.example.com \
  API_KEY='replace-with-a-long-random-secret' \
  scripts/manage.sh install-service
```

Daily operations:

```bash
sudo scripts/manage.sh start
sudo scripts/manage.sh stop
sudo scripts/manage.sh restart
scripts/manage.sh status
scripts/manage.sh api-check
scripts/manage.sh logs
sudo PORT=5082 scripts/manage.sh configure
sudo APP_DIR=/opt/musicdecrypto/backend scripts/manage.sh configure
sudo UPDATE_DIR=/srv/musicdecrypto/updates scripts/manage.sh configure
sudo ALLOWED_ORIGINS=https://app.example.com,https://admin.example.com scripts/manage.sh configure
sudo scripts/manage.sh reinstall-deps
sudo scripts/manage.sh uninstall
```

`configure` updates the installed environment file, rewrites the systemd unit when paths or ports change, reloads systemd, and restarts the service. Variables not provided on the command line are kept from the existing installed configuration.

To remove service files and data:

```bash
sudo REMOVE_DATA=1 scripts/manage.sh uninstall
```

Install Nginx reverse proxy separately if this service is exposed publicly:

```bash
sudo cp deploy/nginx/musicdecrypto-backend.conf /etc/nginx/sites-available/musicdecrypto-backend.conf
sudo ln -s /etc/nginx/sites-available/musicdecrypto-backend.conf /etc/nginx/sites-enabled/musicdecrypto-backend.conf
sudo nginx -t
sudo systemctl reload nginx
```

Edit `server_name example.com;` before enabling HTTPS.

## Notes

- tus resumable upload files are stored under `TempRoot/tus`.
- Uploaded files are copied into `StorageRoot/uploads`.
- Decrypted files are written under `StorageRoot/outputs/{jobId}`.
- Files uploaded through `/update` are stored under `UpdateRoot/{batchId}`.
- Applying a batch copies manifest-listed files into `UpdateApplyRoot`, which defaults to the app directory used by the systemd service. The `/update` apply action then runs `scripts/manage.sh publish` in the background and stops the app; with the generated systemd unit, `Restart=always` starts the newly published service.
- Job state is stored in `StorageRoot/state/jobs.json`.
- The current worker processes jobs one at a time, which is conservative for CPU and disk usage on a small VPS.
