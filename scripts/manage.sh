#!/usr/bin/env bash

if [ -z "${BASH_VERSION:-}" ]; then
  if command -v bash >/dev/null 2>&1; then
    exec bash "$0" "$@"
  fi

  echo "Error: bash is required to run this script." >&2
  exit 1
fi

set -euo pipefail

PROJECT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

default_service_user() {
  local owner
  owner="$(stat -c '%U' "$PROJECT_DIR" 2>/dev/null || true)"
  if [ -n "$owner" ] && [ "$owner" != "root" ]; then
    printf '%s\n' "$owner"
  else
    printf 'musicdecrypto\n'
  fi
}

default_service_group() {
  local user="$1"
  id -gn "$user" 2>/dev/null || printf '%s\n' "$user"
}

DEFAULT_SERVICE_USER="$(default_service_user)"

PROVIDED_SERVICE_NAME="${SERVICE_NAME+x}"
PROVIDED_SERVICE_USER="${SERVICE_USER+x}"
PROVIDED_SERVICE_GROUP="${SERVICE_GROUP+x}"
PROVIDED_APP_DIR="${APP_DIR+x}"
PROVIDED_PUBLISH_DIR="${PUBLISH_DIR+x}"
PROVIDED_DATA_DIR="${DATA_DIR+x}"
PROVIDED_TEMP_DIR="${TEMP_DIR+x}"
PROVIDED_UPDATE_DIR="${UPDATE_DIR+x}"
PROVIDED_APPLY_DIR="${APPLY_DIR+x}"
PROVIDED_FRONTEND_SOURCE_DIR="${FRONTEND_SOURCE_DIR+x}"
PROVIDED_FRONTEND_DIR="${FRONTEND_DIR+x}"
PROVIDED_BIND_HOST="${BIND_HOST+x}"
PROVIDED_PORT="${PORT+x}"
PROVIDED_API_KEY="${API_KEY+x}"
PROVIDED_ALLOWED_ORIGINS="${ALLOWED_ORIGINS+x}"
PROVIDED_SERVER_NAME="${SERVER_NAME+x}"
PROVIDED_SSL_CERTIFICATE="${SSL_CERTIFICATE+x}"
PROVIDED_SSL_CERTIFICATE_KEY="${SSL_CERTIFICATE_KEY+x}"
PROVIDED_NGINX_SITE_FILE="${NGINX_SITE_FILE+x}"
PROVIDED_NGINX_USER="${NGINX_USER+x}"
PROVIDED_FRONTEND_BUILD="${FRONTEND_BUILD+x}"
PROVIDED_PNPM_BIN="${PNPM_BIN+x}"
PROVIDED_FORCE_OVERWRITE="${FORCE_OVERWRITE+x}"
PROVIDED_EXTENSIVE_DETECTION="${EXTENSIVE_DETECTION+x}"
PROVIDED_AUTO_DELETE_AFTER_DAYS="${AUTO_DELETE_AFTER_DAYS+x}"
PROVIDED_PACKAGE_DIR="${PACKAGE_DIR+x}"

SERVICE_NAME="${SERVICE_NAME:-musicdecrypto-backend}"
SERVICE_USER="${SERVICE_USER:-$DEFAULT_SERVICE_USER}"
SERVICE_GROUP="${SERVICE_GROUP:-$(default_service_group "$SERVICE_USER")}"
APP_DIR="${APP_DIR:-$PROJECT_DIR}"
PUBLISH_DIR="${PUBLISH_DIR:-$APP_DIR/publish}"
DATA_DIR="${DATA_DIR:-/var/lib/musicdecrypto}"
TEMP_DIR="${TEMP_DIR:-/var/tmp/musicdecrypto}"
UPDATE_DIR="${UPDATE_DIR:-$DATA_DIR/updates}"
APPLY_DIR="${APPLY_DIR:-$APP_DIR}"
FRONTEND_SOURCE_DIR="${FRONTEND_SOURCE_DIR:-$(cd "$PROJECT_DIR/../frontend" && pwd)}"
FRONTEND_DIR="${FRONTEND_DIR:-$APP_DIR/frontend-dist}"
BIND_HOST="${BIND_HOST:-127.0.0.1}"
PORT="${PORT:-5080}"
API_KEY="${API_KEY:-}"
ALLOWED_ORIGINS="${ALLOWED_ORIGINS:-}"
SERVER_NAME="${SERVER_NAME:-}"
SSL_CERTIFICATE="${SSL_CERTIFICATE:-}"
SSL_CERTIFICATE_KEY="${SSL_CERTIFICATE_KEY:-}"
NGINX_SITE_FILE="${NGINX_SITE_FILE:-/etc/nginx/sites-available/musicdecrypto.conf}"
NGINX_USER="${NGINX_USER:-}"
FRONTEND_BUILD="${FRONTEND_BUILD:-1}"
PNPM_BIN="${PNPM_BIN:-}"
FORCE_OVERWRITE="${FORCE_OVERWRITE:-true}"
EXTENSIVE_DETECTION="${EXTENSIVE_DETECTION:-false}"
AUTO_DELETE_AFTER_DAYS="${AUTO_DELETE_AFTER_DAYS:-7}"
PACKAGE_ARCHIVE="${PACKAGE_ARCHIVE:-$PROJECT_DIR/deploy/package/musicdecrypto-linux-x64.tar.gz}"
PACKAGE_DIR="${PACKAGE_DIR:-$APP_DIR/package}"
ENV_FILE="${ENV_FILE:-/etc/$SERVICE_NAME.env}"
SERVICE_FILE="${SERVICE_FILE:-/etc/systemd/system/$SERVICE_NAME.service}"
DOTNET_CHANNEL="${DOTNET_CHANNEL:-10.0}"
DOTNET_INSTALL_DIR="${DOTNET_INSTALL_DIR:-/opt/dotnet}"
DOTNET_INSTALL_SCRIPT_URL="${DOTNET_INSTALL_SCRIPT_URL:-https://dot.net/v1/dotnet-install.sh}"

usage() {
  cat <<USAGE
Usage: $0 <command>

Setup commands:
  env-check        Check OS, tools, .NET, package archive, paths, and config
  install-deps     Install deployment dependencies on Ubuntu/Debian
  publish          Restore and publish the ASP.NET backend
  extract-package  Extract deploy/package archive to PACKAGE_DIR
  install-service  Create user, directories, env file, systemd service, and start it
  publish-frontend Build and copy the frontend into FRONTEND_DIR
  write-web-config Generate the Nginx site file without reloading Nginx
  install-web      Publish frontend, write Nginx config, and reload Nginx
  install-all      Install backend service and web frontend

Runtime commands:
  start            Start the systemd service
  stop             Stop the systemd service
  restart          Restart the systemd service
  status           Show systemd status
  api-check        Check /healthz and /api/jobs
  logs             Follow service logs

Maintenance commands:
  configure        Update runtime settings and restart the service
  reinstall-deps   Re-run dependency installation
  uninstall        Stop and remove the systemd service; keeps data by default
  show-config      Print resolved configuration

Common settings:
  APP_DIR=$APP_DIR
  DATA_DIR=$DATA_DIR
  TEMP_DIR=$TEMP_DIR
  UPDATE_DIR=$UPDATE_DIR
  APPLY_DIR=$APPLY_DIR
  FRONTEND_SOURCE_DIR=$FRONTEND_SOURCE_DIR
  FRONTEND_DIR=$FRONTEND_DIR
  BIND_HOST=$BIND_HOST
  PORT=$PORT
  API_KEY=<secret>
  SERVER_NAME=<required-for-install-web>
  SSL_CERTIFICATE=/path/to/fullchain.pem optional
  SSL_CERTIFICATE_KEY=/path/to/privkey.pem optional
  NGINX_SITE_FILE=$NGINX_SITE_FILE
  NGINX_USER=$NGINX_USER
  FRONTEND_BUILD=$FRONTEND_BUILD
  PNPM_BIN=/path/to/pnpm optional
  AUTO_DELETE_AFTER_DAYS=$AUTO_DELETE_AFTER_DAYS
  ALLOWED_ORIGINS=http://localhost:5173,http://127.0.0.1:5173
  PACKAGE_ARCHIVE=$PACKAGE_ARCHIVE
  DOTNET_CHANNEL=$DOTNET_CHANNEL
  DOTNET_INSTALL_DIR=$DOTNET_INSTALL_DIR
  DOTNET_INSTALL_SCRIPT_URL=$DOTNET_INSTALL_SCRIPT_URL

Examples:
  sudo API_KEY='replace-with-secret' PORT=5080 $0 install-service
  sudo PORT=5080 $0 install-service
  sudo PORT=5081 ALLOWED_ORIGINS=https://app.example.com $0 configure
  sudo PORT=5081 SERVER_NAME=your-domain.example $0 install-web
  sudo API_KEY='replace-with-secret' PORT=5081 SERVER_NAME=your-domain.example $0 install-all
  $0 api-check
  sudo REMOVE_DATA=1 $0 uninstall
USAGE
}

log() {
  printf '[%s] %s\n' "$(date +'%Y-%m-%d %H:%M:%S')" "$*"
}

fail() {
  printf 'Error: %s\n' "$*" >&2
  exit 1
}

require_root() {
  if [ "${EUID:-$(id -u)}" -ne 0 ]; then
    fail "this command must be run as root, usually with sudo"
  fi
}

have() {
  command -v "$1" >/dev/null 2>&1
}

default_nginx_user() {
  if [ -f /etc/nginx/nginx.conf ]; then
    local configured_user
    configured_user="$(awk '$1 == "user" {gsub(";", "", $2); print $2; exit}' /etc/nginx/nginx.conf)"
    if [ -n "$configured_user" ] && id "$configured_user" >/dev/null 2>&1; then
      printf '%s\n' "$configured_user"
      return
    fi
  fi

  for candidate in www-data nginx http; do
    if id "$candidate" >/dev/null 2>&1; then
      printf '%s\n' "$candidate"
      return
    fi
  done

  true
}

dotnet_cmd() {
  if [ -n "${DOTNET_BIN:-}" ]; then
    printf '%s\n' "$DOTNET_BIN"
    return
  fi

  if command -v dotnet >/dev/null 2>&1; then
    command -v dotnet
    return
  fi

  if [ -x "$DOTNET_INSTALL_DIR/dotnet" ]; then
    printf '%s\n' "$DOTNET_INSTALL_DIR/dotnet"
    return
  fi

  true
}

pnpm_cmd() {
  if [ -n "$PNPM_BIN" ]; then
    printf '%s\n' "$PNPM_BIN"
    return
  fi

  if command -v pnpm >/dev/null 2>&1; then
    command -v pnpm
    return
  fi

  if command -v npm >/dev/null 2>&1; then
    local npm_prefix
    npm_prefix="$(npm config get prefix 2>/dev/null || true)"
    if [ -x "$npm_prefix/bin/pnpm" ]; then
      printf '%s\n' "$npm_prefix/bin/pnpm"
      return
    fi
  fi

  for candidate in \
    /usr/local/bin/pnpm \
    /usr/bin/pnpm \
    /root/.local/share/pnpm/pnpm \
    "$HOME/.local/share/pnpm/pnpm"
  do
    if [ -x "$candidate" ]; then
      printf '%s\n' "$candidate"
      return
    fi
  done

  if command -v corepack >/dev/null 2>&1; then
    printf 'corepack pnpm\n'
    return
  fi

  true
}

service_env() {
  if [ -f "$ENV_FILE" ]; then
    # shellcheck disable=SC1090
    set -a && . "$ENV_FILE" && set +a
  fi
}

generate_api_key() {
  if have openssl; then
    openssl rand -hex 16
    return
  fi

  od -An -N16 -tx1 /dev/urandom | tr -d ' \n'
}

append_allowed_origins() {
  local env_file="$1"
  local index=0
  local origin

  if [ -z "$ALLOWED_ORIGINS" ]; then
    return
  fi

  IFS=',' read -ra origins <<<"$ALLOWED_ORIGINS"
  for origin in "${origins[@]}"; do
    origin="${origin#"${origin%%[![:space:]]*}"}"
    origin="${origin%"${origin##*[![:space:]]}"}"
    if [ -n "$origin" ]; then
      printf 'MusicDecrypto__AllowedOrigins__%s=%s\n' "$index" "$origin" >>"$env_file"
      index=$((index + 1))
    fi
  done
}

env_value() {
  local key="$1"
  local file="${2:-$ENV_FILE}"

  if [ ! -r "$file" ]; then
    return
  fi

  awk -v key="$key" -F= '$1 == key {print substr($0, index($0, "=") + 1)}' "$file" | tail -n 1
}

configured_bind_host() {
  local url
  url="$(env_value Kestrel__Endpoints__Http__Url || true)"
  url="${url#http://}"
  url="${url#https://}"
  url="${url%%/*}"
  url="${url%:*}"

  if [ -n "$url" ]; then
    printf '%s\n' "$url"
  fi
}

configured_port() {
  local url
  url="$(env_value Kestrel__Endpoints__Http__Url || true)"
  url="${url#http://}"
  url="${url#https://}"
  url="${url%%/*}"

  if [[ "$url" == *:* ]]; then
    printf '%s\n' "${url##*:}"
  fi
}

configured_service_value() {
  local key="$1"
  if [ ! -f "$SERVICE_FILE" ]; then
    return
  fi

  awk -F= -v key="$key" '$1 == key {print substr($0, index($0, "=") + 1)}' "$SERVICE_FILE" | tail -n 1
}

configured_allowed_origins() {
  local file="${1:-$ENV_FILE}"

  if [ ! -r "$file" ]; then
    return
  fi

  awk -F= '
    $1 ~ /^MusicDecrypto__AllowedOrigins__[0-9]+$/ {
      values[$1] = substr($0, index($0, "=") + 1)
    }
    END {
      for (i = 0; ; i++) {
        key = "MusicDecrypto__AllowedOrigins__" i
        if (!(key in values)) {
          break
        }
        if (i > 0) {
          printf ","
        }
        printf "%s", values[key]
      }
    }
  ' "$file"
}

was_provided() {
  local name="$1"
  local flag="PROVIDED_$name"
  [ -n "${!flag:-}" ]
}

resolve_runtime_config() {
  if [ ! -f "$ENV_FILE" ]; then
    return
  fi

  if ! was_provided SERVICE_USER; then
    SERVICE_USER="$(configured_service_value User || true)"
    SERVICE_USER="${SERVICE_USER:-$DEFAULT_SERVICE_USER}"
  fi

  if ! was_provided SERVICE_GROUP; then
    SERVICE_GROUP="$(configured_service_value Group || true)"
    SERVICE_GROUP="${SERVICE_GROUP:-$(default_service_group "$SERVICE_USER")}"
  fi

  if ! was_provided BIND_HOST; then
    BIND_HOST="$(env_value MUSICDECRYPTO_MANAGE_BIND_HOST || true)"
    BIND_HOST="${BIND_HOST:-$(configured_bind_host || true)}"
    BIND_HOST="${BIND_HOST:-127.0.0.1}"
  fi

  if ! was_provided PORT; then
    PORT="$(env_value MUSICDECRYPTO_MANAGE_PORT || true)"
    PORT="${PORT:-$(configured_port || true)}"
    PORT="${PORT:-5080}"
  fi

  if ! was_provided APP_DIR; then
    APP_DIR="$(env_value MUSICDECRYPTO_MANAGE_APP_DIR || true)"
    APP_DIR="${APP_DIR:-$(env_value MusicDecrypto__UpdateApplyRoot || true)}"
    APP_DIR="${APP_DIR:-$PROJECT_DIR}"
  fi

  if ! was_provided PUBLISH_DIR; then
    PUBLISH_DIR="$(env_value MUSICDECRYPTO_MANAGE_PUBLISH_DIR || true)"
    PUBLISH_DIR="${PUBLISH_DIR:-$APP_DIR/publish}"
  fi

  if ! was_provided DATA_DIR; then
    DATA_DIR="$(env_value MusicDecrypto__StorageRoot || true)"
    DATA_DIR="${DATA_DIR:-/var/lib/musicdecrypto}"
  fi

  if ! was_provided TEMP_DIR; then
    TEMP_DIR="$(env_value MusicDecrypto__TempRoot || true)"
    TEMP_DIR="${TEMP_DIR:-/var/tmp/musicdecrypto}"
  fi

  if ! was_provided UPDATE_DIR; then
    UPDATE_DIR="$(env_value MusicDecrypto__UpdateRoot || true)"
    UPDATE_DIR="${UPDATE_DIR:-$DATA_DIR/updates}"
  fi

  if ! was_provided APPLY_DIR; then
    APPLY_DIR="$(env_value MusicDecrypto__UpdateApplyRoot || true)"
    APPLY_DIR="${APPLY_DIR:-$APP_DIR}"
  fi

  if ! was_provided FRONTEND_SOURCE_DIR; then
    FRONTEND_SOURCE_DIR="$(env_value MUSICDECRYPTO_MANAGE_FRONTEND_SOURCE_DIR || true)"
    FRONTEND_SOURCE_DIR="${FRONTEND_SOURCE_DIR:-$(cd "$PROJECT_DIR/../frontend" && pwd)}"
  fi

  if ! was_provided FRONTEND_DIR; then
    FRONTEND_DIR="$(env_value MUSICDECRYPTO_MANAGE_FRONTEND_DIR || true)"
    FRONTEND_DIR="${FRONTEND_DIR:-$APP_DIR/frontend-dist}"
  fi

  if ! was_provided PACKAGE_DIR; then
    PACKAGE_DIR="$(env_value MUSICDECRYPTO_MANAGE_PACKAGE_DIR || true)"
    local existing_executable
    existing_executable="$(env_value MusicDecrypto__DecryptoExecutablePath || true)"
    if [ -z "$PACKAGE_DIR" ] && [ -n "$existing_executable" ]; then
      PACKAGE_DIR="$(dirname "$existing_executable")"
    fi
    PACKAGE_DIR="${PACKAGE_DIR:-$APP_DIR/package}"
  fi

  if ! was_provided API_KEY; then
    API_KEY="$(env_value MusicDecrypto__ApiKey || true)"
  fi

  if ! was_provided FORCE_OVERWRITE; then
    FORCE_OVERWRITE="$(env_value MusicDecrypto__ForceOverwrite || true)"
    FORCE_OVERWRITE="${FORCE_OVERWRITE:-true}"
  fi

  if ! was_provided EXTENSIVE_DETECTION; then
    EXTENSIVE_DETECTION="$(env_value MusicDecrypto__ExtensiveDetection || true)"
    EXTENSIVE_DETECTION="${EXTENSIVE_DETECTION:-false}"
  fi

  if ! was_provided AUTO_DELETE_AFTER_DAYS; then
    AUTO_DELETE_AFTER_DAYS="$(env_value MusicDecrypto__AutoDeleteAfterDays || true)"
    AUTO_DELETE_AFTER_DAYS="${AUTO_DELETE_AFTER_DAYS:-7}"
  fi

  if ! was_provided ALLOWED_ORIGINS; then
    ALLOWED_ORIGINS="$(configured_allowed_origins || true)"
  fi

  if ! was_provided SERVER_NAME; then
    SERVER_NAME="$(env_value MUSICDECRYPTO_MANAGE_SERVER_NAME || true)"
  fi

  if ! was_provided SSL_CERTIFICATE; then
    SSL_CERTIFICATE="$(env_value MUSICDECRYPTO_MANAGE_SSL_CERTIFICATE || true)"
  fi

  if ! was_provided SSL_CERTIFICATE_KEY; then
    SSL_CERTIFICATE_KEY="$(env_value MUSICDECRYPTO_MANAGE_SSL_CERTIFICATE_KEY || true)"
  fi

  if ! was_provided NGINX_SITE_FILE; then
    NGINX_SITE_FILE="$(env_value MUSICDECRYPTO_MANAGE_NGINX_SITE_FILE || true)"
    NGINX_SITE_FILE="${NGINX_SITE_FILE:-/etc/nginx/sites-available/musicdecrypto.conf}"
  fi

  if ! was_provided NGINX_USER; then
    NGINX_USER="$(env_value MUSICDECRYPTO_MANAGE_NGINX_USER || true)"
    NGINX_USER="${NGINX_USER:-$(default_nginx_user || true)}"
  fi

  if ! was_provided FRONTEND_BUILD; then
    FRONTEND_BUILD="$(env_value MUSICDECRYPTO_MANAGE_FRONTEND_BUILD || true)"
    FRONTEND_BUILD="${FRONTEND_BUILD:-1}"
  fi

  if ! was_provided PNPM_BIN; then
    PNPM_BIN="$(env_value MUSICDECRYPTO_MANAGE_PNPM_BIN || true)"
  fi
}

cmd_show_config() {
  resolve_runtime_config

  cat <<CONFIG
SERVICE_NAME=$SERVICE_NAME
SERVICE_USER=$SERVICE_USER
APP_DIR=$APP_DIR
PUBLISH_DIR=$PUBLISH_DIR
DATA_DIR=$DATA_DIR
TEMP_DIR=$TEMP_DIR
UPDATE_DIR=$UPDATE_DIR
APPLY_DIR=$APPLY_DIR
FRONTEND_SOURCE_DIR=$FRONTEND_SOURCE_DIR
FRONTEND_DIR=$FRONTEND_DIR
BIND_HOST=$BIND_HOST
PORT=$PORT
PACKAGE_ARCHIVE=$PACKAGE_ARCHIVE
PACKAGE_DIR=$PACKAGE_DIR
ENV_FILE=$ENV_FILE
SERVICE_FILE=$SERVICE_FILE
SERVER_NAME=$SERVER_NAME
SSL_CERTIFICATE=$SSL_CERTIFICATE
SSL_CERTIFICATE_KEY=$SSL_CERTIFICATE_KEY
NGINX_SITE_FILE=$NGINX_SITE_FILE
NGINX_USER=$NGINX_USER
FRONTEND_BUILD=$FRONTEND_BUILD
PNPM_BIN=$PNPM_BIN
FORCE_OVERWRITE=$FORCE_OVERWRITE
EXTENSIVE_DETECTION=$EXTENSIVE_DETECTION
AUTO_DELETE_AFTER_DAYS=$AUTO_DELETE_AFTER_DAYS
ALLOWED_ORIGINS=$ALLOWED_ORIGINS
DOTNET_CHANNEL=$DOTNET_CHANNEL
DOTNET_INSTALL_DIR=$DOTNET_INSTALL_DIR
DOTNET_INSTALL_SCRIPT_URL=$DOTNET_INSTALL_SCRIPT_URL
CONFIG
}

dotnet_has_required_sdk() {
  local dotnet_path
  dotnet_path="$(dotnet_cmd)"
  [ -n "$dotnet_path" ] || return 1

  "$dotnet_path" --list-sdks 2>/dev/null | awk '{print $1}' | grep -Eq "^${DOTNET_CHANNEL//./\\.}\\."
}

cmd_env_check() {
  log "Checking environment"

  [ -f "$PROJECT_DIR/MusicDecrypto.Backend.csproj" ] || fail "project file not found"
  [ -f "$PACKAGE_ARCHIVE" ] || fail "package archive not found: $PACKAGE_ARCHIVE"

  have bash || fail "bash is required"
  have tar || fail "tar is required"
  have gzip || fail "gzip is required"
  have curl || fail "curl is required"

  local dotnet_path
  dotnet_path="$(dotnet_cmd)"
  [ -n "$dotnet_path" ] || fail "dotnet is required; run install-deps"
  "$dotnet_path" --info >/dev/null

  if [ -d "$PACKAGE_DIR" ] && [ ! -x "$PACKAGE_DIR/musicdecrypto" ]; then
    fail "package directory exists but musicdecrypto is not executable: $PACKAGE_DIR"
  fi

  tar -tzf "$PACKAGE_ARCHIVE" './musicdecrypto' >/dev/null ||
    tar -tzf "$PACKAGE_ARCHIVE" 'musicdecrypto' >/dev/null ||
    fail "package archive does not contain musicdecrypto executable"

  log "Environment looks usable"
}

cmd_install_deps() {
  require_root

  if [ ! -f /etc/os-release ]; then
    fail "/etc/os-release not found; automatic dependency install only supports Ubuntu/Debian"
  fi

  # shellcheck disable=SC1091
  . /etc/os-release
  case "${ID:-}" in
    ubuntu|debian) ;;
    *) fail "unsupported OS: ${ID:-unknown}; install .NET 10 SDK/runtime manually" ;;
  esac

  log "Installing base packages"
  apt-get update
  apt-get install -y ca-certificates curl gzip tar wget gpg openssl

  if ! have dotnet; then
    log "Installing Microsoft package feed"
    local repo_deb="/tmp/packages-microsoft-prod.deb"
    wget -O "$repo_deb" "https://packages.microsoft.com/config/${ID}/${VERSION_ID}/packages-microsoft-prod.deb"
    dpkg -i "$repo_deb"
    apt-get update
  fi

  if dotnet_has_required_sdk; then
    log ".NET SDK $DOTNET_CHANNEL is already installed"
    return
  fi

  if apt-cache show "dotnet-sdk-$DOTNET_CHANNEL" >/dev/null 2>&1; then
    log "Installing .NET SDK $DOTNET_CHANNEL from apt"
    apt-get install -y "dotnet-sdk-$DOTNET_CHANNEL"
    return
  fi

  log "apt package dotnet-sdk-$DOTNET_CHANNEL was not found; installing with Microsoft dotnet-install script"
  log "Download script: $DOTNET_INSTALL_SCRIPT_URL"
  log "Install directory: $DOTNET_INSTALL_DIR"
  mkdir -p "$DOTNET_INSTALL_DIR"
  curl -fsSL "$DOTNET_INSTALL_SCRIPT_URL" -o /tmp/dotnet-install.sh
  bash /tmp/dotnet-install.sh --channel "$DOTNET_CHANNEL" --quality GA --install-dir "$DOTNET_INSTALL_DIR"
  ln -sf "$DOTNET_INSTALL_DIR/dotnet" /usr/local/bin/dotnet
}

cmd_publish() {
  local dotnet_path
  dotnet_path="$(dotnet_cmd)"
  [ -n "$dotnet_path" ] || fail "dotnet is required; run install-deps first"

  log "Publishing backend to $PUBLISH_DIR"
  mkdir -p "$PUBLISH_DIR"
  "$dotnet_path" restore "$PROJECT_DIR/MusicDecrypto.Backend.csproj"
  "$dotnet_path" publish "$PROJECT_DIR/MusicDecrypto.Backend.csproj" -c Release -o "$PUBLISH_DIR"
}

cmd_extract_package() {
  [ -f "$PACKAGE_ARCHIVE" ] || fail "package archive not found: $PACKAGE_ARCHIVE"

  log "Extracting package archive to $PACKAGE_DIR"
  rm -rf "$PACKAGE_DIR"
  mkdir -p "$PACKAGE_DIR"
  tar -xzf "$PACKAGE_ARCHIVE" -C "$PACKAGE_DIR"
  chmod +x "$PACKAGE_DIR/musicdecrypto"
}

ensure_nginx_can_read_frontend() {
  if [ "${EUID:-$(id -u)}" -ne 0 ]; then
    return
  fi

  if [ -z "$NGINX_USER" ]; then
    log "Nginx user was not detected. If Nginx returns permission denied, set NGINX_USER=www-data or your Nginx worker user."
    chmod -R a+rX "$FRONTEND_DIR"
    return
  fi

  log "Granting Nginx user '$NGINX_USER' access to frontend files"
  chown -R "$NGINX_USER:$NGINX_USER" "$FRONTEND_DIR"
  find "$FRONTEND_DIR" -type d -exec chmod 755 {} +
  find "$FRONTEND_DIR" -type f -exec chmod 644 {} +

  local path="$FRONTEND_DIR"
  while [ "$path" != "/" ]; do
    chmod a+x "$path" 2>/dev/null || true
    path="$(dirname "$path")"
  done
}

cmd_publish_frontend() {
  resolve_runtime_config

  local source_dist="$FRONTEND_SOURCE_DIR/dist"

  [ -d "$FRONTEND_SOURCE_DIR" ] || fail "frontend source directory not found: $FRONTEND_SOURCE_DIR"
  [ -f "$FRONTEND_SOURCE_DIR/package.json" ] || fail "frontend package.json not found: $FRONTEND_SOURCE_DIR/package.json"

  if [ "$FRONTEND_BUILD" != "0" ]; then
    local pnpm_path
    pnpm_path="$(pnpm_cmd)"
    [ -n "$pnpm_path" ] || fail "pnpm is required to build the frontend. If pnpm is installed for another user, pass PNPM_BIN=/path/to/pnpm, run with sudo -E, or run with FRONTEND_BUILD=0 after preparing $source_dist."

    log "Installing frontend dependencies"
    $pnpm_path -C "$FRONTEND_SOURCE_DIR" install --frozen-lockfile

    log "Building frontend"
    $pnpm_path -C "$FRONTEND_SOURCE_DIR" build
  fi

  [ -f "$source_dist/index.html" ] || fail "frontend build output not found: $source_dist/index.html"

  log "Publishing frontend to $FRONTEND_DIR"
  rm -rf "$FRONTEND_DIR"
  mkdir -p "$FRONTEND_DIR"
  cp -a "$source_dist/." "$FRONTEND_DIR/"

  ensure_nginx_can_read_frontend
}

write_nginx_file() {
  resolve_runtime_config

  [ -n "$SERVER_NAME" ] || fail "SERVER_NAME is required for install-web, for example: SERVER_NAME=dec.example.com"
  if { [ -n "$SSL_CERTIFICATE" ] && [ -z "$SSL_CERTIFICATE_KEY" ]; } ||
    { [ -z "$SSL_CERTIFICATE" ] && [ -n "$SSL_CERTIFICATE_KEY" ]; }; then
    fail "provide both SSL_CERTIFICATE and SSL_CERTIFICATE_KEY, or leave both empty"
  fi

  log "Writing $NGINX_SITE_FILE"
  mkdir -p "$(dirname "$NGINX_SITE_FILE")"

  if [ -n "$SSL_CERTIFICATE" ]; then
    log "Generating HTTPS Nginx site. Certificate=$SSL_CERTIFICATE Key=$SSL_CERTIFICATE_KEY"
    cat >"$NGINX_SITE_FILE" <<NGINX
server {
    listen 80;
    server_name $SERVER_NAME;

    return 301 https://\$host\$request_uri;
}

server {
    listen 443 ssl http2;
    server_name $SERVER_NAME;

    ssl_certificate $SSL_CERTIFICATE;
    ssl_certificate_key $SSL_CERTIFICATE_KEY;

    root $FRONTEND_DIR;
    index index.html;

    client_max_body_size 0;

    location /_musicdecrypto_outputs/ {
        internal;
        alias $DATA_DIR/outputs/;
    }

    location /api/ {
        proxy_pass http://$BIND_HOST:$PORT;
        proxy_http_version 1.1;
        proxy_set_header Host \$host;
        proxy_set_header X-Real-IP \$remote_addr;
        proxy_set_header X-Forwarded-For \$proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto https;
        proxy_request_buffering off;
        proxy_buffering off;
        proxy_read_timeout 3600s;
        proxy_send_timeout 3600s;
    }

    location /files {
        proxy_pass http://$BIND_HOST:$PORT;
        proxy_http_version 1.1;
        proxy_set_header Host \$host;
        proxy_set_header X-Real-IP \$remote_addr;
        proxy_set_header X-Forwarded-For \$proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto https;
        proxy_request_buffering off;
        proxy_buffering off;
        proxy_read_timeout 3600s;
        proxy_send_timeout 3600s;
    }

    location /healthz {
        proxy_pass http://$BIND_HOST:$PORT;
        proxy_http_version 1.1;
        proxy_set_header Host \$host;
        proxy_set_header X-Real-IP \$remote_addr;
        proxy_set_header X-Forwarded-For \$proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto https;
    }

    location /update {
        proxy_pass http://$BIND_HOST:$PORT;
        proxy_http_version 1.1;
        proxy_set_header Host \$host;
        proxy_set_header X-Real-IP \$remote_addr;
        proxy_set_header X-Forwarded-For \$proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto https;
        proxy_request_buffering off;
        proxy_buffering off;
        proxy_read_timeout 3600s;
        proxy_send_timeout 3600s;
    }

    location / {
        try_files \$uri \$uri/ /index.html;
    }
}
NGINX
  else
    log "Generating HTTP Nginx site. No SSL_CERTIFICATE/SSL_CERTIFICATE_KEY were provided."
    cat >"$NGINX_SITE_FILE" <<NGINX
server {
    listen 80;
    server_name $SERVER_NAME;

    root $FRONTEND_DIR;
    index index.html;

    client_max_body_size 0;

    location /_musicdecrypto_outputs/ {
        internal;
        alias $DATA_DIR/outputs/;
    }

    location /api/ {
        proxy_pass http://$BIND_HOST:$PORT;
        proxy_http_version 1.1;
        proxy_set_header Host \$host;
        proxy_set_header X-Real-IP \$remote_addr;
        proxy_set_header X-Forwarded-For \$proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto \$scheme;
        proxy_request_buffering off;
        proxy_buffering off;
        proxy_read_timeout 3600s;
        proxy_send_timeout 3600s;
    }

    location /files {
        proxy_pass http://$BIND_HOST:$PORT;
        proxy_http_version 1.1;
        proxy_set_header Host \$host;
        proxy_set_header X-Real-IP \$remote_addr;
        proxy_set_header X-Forwarded-For \$proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto \$scheme;
        proxy_request_buffering off;
        proxy_buffering off;
        proxy_read_timeout 3600s;
        proxy_send_timeout 3600s;
    }

    location /healthz {
        proxy_pass http://$BIND_HOST:$PORT;
        proxy_http_version 1.1;
        proxy_set_header Host \$host;
        proxy_set_header X-Real-IP \$remote_addr;
        proxy_set_header X-Forwarded-For \$proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto \$scheme;
    }

    location /update {
        proxy_pass http://$BIND_HOST:$PORT;
        proxy_http_version 1.1;
        proxy_set_header Host \$host;
        proxy_set_header X-Real-IP \$remote_addr;
        proxy_set_header X-Forwarded-For \$proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto \$scheme;
        proxy_request_buffering off;
        proxy_buffering off;
        proxy_read_timeout 3600s;
        proxy_send_timeout 3600s;
    }

    location / {
        try_files \$uri \$uri/ /index.html;
    }
}
NGINX
  fi

  if [ -d /etc/nginx/sites-enabled ] && [[ "$NGINX_SITE_FILE" == /etc/nginx/sites-available/* ]]; then
    ln -sf "$NGINX_SITE_FILE" "/etc/nginx/sites-enabled/$(basename "$NGINX_SITE_FILE")"
  fi
}

cmd_write_web_config() {
  write_nginx_file
}

cmd_install_web() {
  require_root

  cmd_publish_frontend
  write_nginx_file

  if have nginx; then
    nginx -t
    systemctl reload nginx
  else
    log "nginx command not found; install/reload your web server manually."
  fi
}

cmd_install_all() {
  require_root

  cmd_install_service
  cmd_install_web
}

ensure_service_user() {
  if ! getent group "$SERVICE_GROUP" >/dev/null; then
    groupadd --system "$SERVICE_GROUP"
  fi

  if ! id "$SERVICE_USER" >/dev/null 2>&1; then
    useradd --system --create-home --home-dir "$DATA_DIR" --shell /usr/sbin/nologin --gid "$SERVICE_GROUP" "$SERVICE_USER"
  fi
}

write_env_file() {
  resolve_runtime_config

  local effective_api_key="$API_KEY"
  if [ -z "$effective_api_key" ] && [ -f "$ENV_FILE" ]; then
    local existing_api_key
    existing_api_key="$(awk -F= '$1 == "MusicDecrypto__ApiKey" {print substr($0, index($0, "=") + 1)}' "$ENV_FILE" | tail -n 1)"
    if [ -n "$existing_api_key" ]; then
      effective_api_key="$existing_api_key"
      log "API_KEY was not provided; reusing the existing API key from $ENV_FILE."
    fi
  fi

  if [ -z "$effective_api_key" ]; then
    effective_api_key="$(generate_api_key)"
    log "API_KEY was not provided; generated a random 32-character API key."
    log "Generated API key: $effective_api_key"
  fi

  log "Writing $ENV_FILE"
  cat >"$ENV_FILE" <<ENV
ASPNETCORE_ENVIRONMENT=Production
MUSICDECRYPTO_MANAGE_BIND_HOST=$BIND_HOST
MUSICDECRYPTO_MANAGE_PORT=$PORT
MUSICDECRYPTO_MANAGE_APP_DIR=$APP_DIR
MUSICDECRYPTO_MANAGE_PUBLISH_DIR=$PUBLISH_DIR
MUSICDECRYPTO_MANAGE_PACKAGE_DIR=$PACKAGE_DIR
MUSICDECRYPTO_MANAGE_FRONTEND_SOURCE_DIR=$FRONTEND_SOURCE_DIR
MUSICDECRYPTO_MANAGE_FRONTEND_DIR=$FRONTEND_DIR
MUSICDECRYPTO_MANAGE_SERVER_NAME=$SERVER_NAME
MUSICDECRYPTO_MANAGE_SSL_CERTIFICATE=$SSL_CERTIFICATE
MUSICDECRYPTO_MANAGE_SSL_CERTIFICATE_KEY=$SSL_CERTIFICATE_KEY
MUSICDECRYPTO_MANAGE_NGINX_SITE_FILE=$NGINX_SITE_FILE
MUSICDECRYPTO_MANAGE_NGINX_USER=$NGINX_USER
MUSICDECRYPTO_MANAGE_FRONTEND_BUILD=$FRONTEND_BUILD
MUSICDECRYPTO_MANAGE_PNPM_BIN=$PNPM_BIN
Kestrel__Endpoints__Http__Url=http://$BIND_HOST:$PORT
MusicDecrypto__StorageRoot=$DATA_DIR
MusicDecrypto__TempRoot=$TEMP_DIR
MusicDecrypto__UpdateRoot=$UPDATE_DIR
MusicDecrypto__UpdateApplyRoot=$APPLY_DIR
MusicDecrypto__DecryptoExecutablePath=$PACKAGE_DIR/musicdecrypto
MusicDecrypto__ApiKey=$effective_api_key
MusicDecrypto__ForceOverwrite=$FORCE_OVERWRITE
MusicDecrypto__ExtensiveDetection=$EXTENSIVE_DETECTION
MusicDecrypto__AutoDeleteAfterDays=$AUTO_DELETE_AFTER_DAYS
ENV
  append_allowed_origins "$ENV_FILE"
  chmod 600 "$ENV_FILE"
}

cmd_configure() {
  require_root

  if [ ! -f "$ENV_FILE" ]; then
    fail "environment file not found: $ENV_FILE; run install-service first"
  fi

  log "Updating runtime configuration"
  write_env_file
  write_service_file
  mkdir -p "$DATA_DIR" "$TEMP_DIR" "$UPDATE_DIR" "$APPLY_DIR"
  chown -R "$SERVICE_USER:$SERVICE_GROUP" "$DATA_DIR" "$TEMP_DIR" "$UPDATE_DIR" "$APPLY_DIR"

  systemctl daemon-reload
  systemctl restart "$SERVICE_NAME"
  systemctl --no-pager --full status "$SERVICE_NAME"
}

write_service_file() {
  log "Writing $SERVICE_FILE"
  cat >"$SERVICE_FILE" <<SERVICE
[Unit]
Description=MusicDecrypto Backend
After=network.target

[Service]
Type=simple
User=$SERVICE_USER
Group=$SERVICE_GROUP
WorkingDirectory=$APP_DIR
EnvironmentFile=$ENV_FILE
Environment=PATH=$DOTNET_INSTALL_DIR:/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin
ExecStart=/usr/bin/env dotnet $PUBLISH_DIR/MusicDecrypto.Backend.dll
Restart=always
RestartSec=5

NoNewPrivileges=true
PrivateTmp=true
ProtectSystem=full
ReadWritePaths=-$DATA_DIR -$TEMP_DIR -$UPDATE_DIR -$APPLY_DIR

[Install]
WantedBy=multi-user.target
SERVICE
}

cmd_install_service() {
  require_root

  cmd_env_check
  ensure_service_user

  log "Creating directories"
  mkdir -p "$APP_DIR" "$DATA_DIR" "$TEMP_DIR" "$UPDATE_DIR" "$APPLY_DIR"
  chown -R "$SERVICE_USER:$SERVICE_GROUP" "$DATA_DIR" "$TEMP_DIR" "$UPDATE_DIR" "$APPLY_DIR"

  cmd_publish
  cmd_extract_package
  write_env_file
  write_service_file

  chown -R "$SERVICE_USER:$SERVICE_GROUP" "$APP_DIR" "$DATA_DIR" "$TEMP_DIR" "$UPDATE_DIR" "$APPLY_DIR"

  log "Enabling service"
  systemctl daemon-reload
  systemctl enable "$SERVICE_NAME"
  systemctl restart "$SERVICE_NAME"
  systemctl --no-pager --full status "$SERVICE_NAME"
}

cmd_start() {
  require_root
  systemctl start "$SERVICE_NAME"
}

cmd_stop() {
  require_root
  systemctl stop "$SERVICE_NAME"
}

cmd_restart() {
  require_root
  systemctl restart "$SERVICE_NAME"
}

cmd_status() {
  systemctl --no-pager --full status "$SERVICE_NAME"
}

cmd_logs() {
  journalctl -u "$SERVICE_NAME" -n "${LINES:-200}" -f
}

cmd_api_check() {
  service_env

  local check_host="${MUSICDECRYPTO_MANAGE_BIND_HOST:-$BIND_HOST}"
  local check_port="${MUSICDECRYPTO_MANAGE_PORT:-$PORT}"
  local base_url="http://$check_host:$check_port"
  local auth_args=()
  if [ -n "${MusicDecrypto__ApiKey:-}" ]; then
    auth_args=(-H "X-Api-Key: $MusicDecrypto__ApiKey")
  elif [ -n "$API_KEY" ]; then
    auth_args=(-H "X-Api-Key: $API_KEY")
  fi

  log "Checking $base_url/healthz"
  curl -fsS "$base_url/healthz"
  printf '\n'

  log "Checking $base_url/api/jobs"
  curl -fsS "${auth_args[@]}" "$base_url/api/jobs"
  printf '\n'
}

cmd_uninstall() {
  require_root

  log "Stopping and disabling $SERVICE_NAME"
  systemctl stop "$SERVICE_NAME" >/dev/null 2>&1 || true
  systemctl disable "$SERVICE_NAME" >/dev/null 2>&1 || true
  rm -f "$SERVICE_FILE" "$ENV_FILE"
  systemctl daemon-reload

  if [ "${REMOVE_DATA:-0}" = "1" ]; then
    log "Removing app, data, and temp directories"
    rm -rf "$APP_DIR" "$DATA_DIR" "$TEMP_DIR" "$UPDATE_DIR" "$APPLY_DIR"
  else
    log "Keeping $APP_DIR, $DATA_DIR, $TEMP_DIR, $UPDATE_DIR, and $APPLY_DIR. Set REMOVE_DATA=1 to remove them."
  fi
}

command="${1:-}"
case "$command" in
  env-check) cmd_env_check ;;
  install-deps) cmd_install_deps ;;
  publish) cmd_publish ;;
  extract-package) cmd_extract_package ;;
  install-service) cmd_install_service ;;
  publish-frontend) cmd_publish_frontend ;;
  write-web-config) cmd_write_web_config ;;
  install-web) cmd_install_web ;;
  install-all) cmd_install_all ;;
  configure) cmd_configure ;;
  start) cmd_start ;;
  stop) cmd_stop ;;
  restart) cmd_restart ;;
  status) cmd_status ;;
  api-check) cmd_api_check ;;
  logs) cmd_logs ;;
  reinstall-deps) cmd_install_deps ;;
  uninstall) cmd_uninstall ;;
  show-config) cmd_show_config ;;
  ""|-h|--help|help) usage ;;
  *) usage >&2; fail "unknown command: $command" ;;
esac
