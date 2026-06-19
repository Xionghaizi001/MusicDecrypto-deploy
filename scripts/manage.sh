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

PROVIDED_SERVICE_NAME="${SERVICE_NAME+x}"
PROVIDED_SERVICE_USER="${SERVICE_USER+x}"
PROVIDED_SERVICE_GROUP="${SERVICE_GROUP+x}"
PROVIDED_APP_DIR="${APP_DIR+x}"
PROVIDED_PUBLISH_DIR="${PUBLISH_DIR+x}"
PROVIDED_DATA_DIR="${DATA_DIR+x}"
PROVIDED_TEMP_DIR="${TEMP_DIR+x}"
PROVIDED_BIND_HOST="${BIND_HOST+x}"
PROVIDED_PORT="${PORT+x}"
PROVIDED_API_KEY="${API_KEY+x}"
PROVIDED_ALLOWED_ORIGINS="${ALLOWED_ORIGINS+x}"
PROVIDED_FORCE_OVERWRITE="${FORCE_OVERWRITE+x}"
PROVIDED_EXTENSIVE_DETECTION="${EXTENSIVE_DETECTION+x}"
PROVIDED_PACKAGE_DIR="${PACKAGE_DIR+x}"

SERVICE_NAME="${SERVICE_NAME:-musicdecrypto-backend}"
SERVICE_USER="${SERVICE_USER:-musicdecrypto}"
SERVICE_GROUP="${SERVICE_GROUP:-$SERVICE_USER}"
APP_DIR="${APP_DIR:-/opt/musicdecrypto/backend}"
PUBLISH_DIR="${PUBLISH_DIR:-$APP_DIR/publish}"
DATA_DIR="${DATA_DIR:-/var/lib/musicdecrypto}"
TEMP_DIR="${TEMP_DIR:-/var/tmp/musicdecrypto}"
BIND_HOST="${BIND_HOST:-127.0.0.1}"
PORT="${PORT:-5080}"
API_KEY="${API_KEY:-}"
ALLOWED_ORIGINS="${ALLOWED_ORIGINS:-}"
FORCE_OVERWRITE="${FORCE_OVERWRITE:-true}"
EXTENSIVE_DETECTION="${EXTENSIVE_DETECTION:-false}"
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
  BIND_HOST=$BIND_HOST
  PORT=$PORT
  API_KEY=<secret>
  ALLOWED_ORIGINS=http://localhost:5173,http://127.0.0.1:5173
  PACKAGE_ARCHIVE=$PACKAGE_ARCHIVE
  DOTNET_CHANNEL=$DOTNET_CHANNEL
  DOTNET_INSTALL_DIR=$DOTNET_INSTALL_DIR
  DOTNET_INSTALL_SCRIPT_URL=$DOTNET_INSTALL_SCRIPT_URL

Examples:
  sudo API_KEY='replace-with-secret' PORT=5080 $0 install-service
  sudo PORT=5080 $0 install-service
  sudo PORT=5081 ALLOWED_ORIGINS=https://app.example.com $0 configure
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

  if [ -x /tmp/dotnet/dotnet ]; then
    printf '%s\n' /tmp/dotnet/dotnet
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

  if [ ! -f "$file" ]; then
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

configured_allowed_origins() {
  local file="${1:-$ENV_FILE}"

  if [ ! -f "$file" ]; then
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

  if ! was_provided DATA_DIR; then
    DATA_DIR="$(env_value MusicDecrypto__StorageRoot || true)"
    DATA_DIR="${DATA_DIR:-/var/lib/musicdecrypto}"
  fi

  if ! was_provided TEMP_DIR; then
    TEMP_DIR="$(env_value MusicDecrypto__TempRoot || true)"
    TEMP_DIR="${TEMP_DIR:-/var/tmp/musicdecrypto}"
  fi

  if ! was_provided PACKAGE_DIR; then
    local existing_executable
    existing_executable="$(env_value MusicDecrypto__DecryptoExecutablePath || true)"
    if [ -n "$existing_executable" ]; then
      PACKAGE_DIR="$(dirname "$existing_executable")"
    fi
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

  if ! was_provided ALLOWED_ORIGINS; then
    ALLOWED_ORIGINS="$(configured_allowed_origins || true)"
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
BIND_HOST=$BIND_HOST
PORT=$PORT
PACKAGE_ARCHIVE=$PACKAGE_ARCHIVE
PACKAGE_DIR=$PACKAGE_DIR
ENV_FILE=$ENV_FILE
SERVICE_FILE=$SERVICE_FILE
FORCE_OVERWRITE=$FORCE_OVERWRITE
EXTENSIVE_DETECTION=$EXTENSIVE_DETECTION
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
Kestrel__Endpoints__Http__Url=http://$BIND_HOST:$PORT
MusicDecrypto__StorageRoot=$DATA_DIR
MusicDecrypto__TempRoot=$TEMP_DIR
MusicDecrypto__DecryptoExecutablePath=$PACKAGE_DIR/musicdecrypto
MusicDecrypto__ApiKey=$effective_api_key
MusicDecrypto__ForceOverwrite=$FORCE_OVERWRITE
MusicDecrypto__ExtensiveDetection=$EXTENSIVE_DETECTION
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
  mkdir -p "$DATA_DIR" "$TEMP_DIR"
  chown -R "$SERVICE_USER:$SERVICE_GROUP" "$DATA_DIR" "$TEMP_DIR"

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
ReadWritePaths=-$DATA_DIR -$TEMP_DIR

[Install]
WantedBy=multi-user.target
SERVICE
}

cmd_install_service() {
  require_root

  cmd_env_check
  ensure_service_user

  log "Creating directories"
  mkdir -p "$APP_DIR" "$DATA_DIR" "$TEMP_DIR"
  chown -R "$SERVICE_USER:$SERVICE_GROUP" "$DATA_DIR" "$TEMP_DIR"

  cmd_publish
  cmd_extract_package
  write_env_file
  write_service_file

  chown -R root:root "$APP_DIR"
  chown -R "$SERVICE_USER:$SERVICE_GROUP" "$DATA_DIR" "$TEMP_DIR"

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
    rm -rf "$APP_DIR" "$DATA_DIR" "$TEMP_DIR"
  else
    log "Keeping $APP_DIR, $DATA_DIR, and $TEMP_DIR. Set REMOVE_DATA=1 to remove them."
  fi
}

command="${1:-}"
case "$command" in
  env-check) cmd_env_check ;;
  install-deps) cmd_install_deps ;;
  publish) cmd_publish ;;
  extract-package) cmd_extract_package ;;
  install-service) cmd_install_service ;;
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
