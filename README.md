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

If `MusicDecrypto:ApiKey` is set, calls under `/files` and `/api` must include either:

- `X-Api-Key: <key>`
- `Authorization: Bearer <key>`

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

## VPS Deployment

These commands assume Ubuntu/Debian. The default installation uses:

- app directory: `/opt/musicdecrypto/backend`
- data directory: `/var/lib/musicdecrypto`
- temporary tus directory: `/var/tmp/musicdecrypto`
- service port: `127.0.0.1:5080`

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
  PORT=5081 \
  DATA_DIR=/srv/musicdecrypto/data \
  TEMP_DIR=/srv/musicdecrypto/tmp \
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
- Job state is stored in `StorageRoot/state/jobs.json`.
- The current worker processes jobs one at a time, which is conservative for CPU and disk usage on a small VPS.
