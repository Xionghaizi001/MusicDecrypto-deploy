#!/usr/bin/env bash
set -euo pipefail

PROJECT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

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
FORCE_OVERWRITE="${FORCE_OVERWRITE:-true}"
EXTENSIVE_DETECTION="${EXTENSIVE_DETECTION:-false}"
PACKAGE_ARCHIVE="${PACKAGE_ARCHIVE:-$PROJECT_DIR/deploy/package/musicdecrypto-linux-x64.tar.gz}"
PACKAGE_DIR="${PACKAGE_DIR:-$APP_DIR/package}"
ENV_FILE="${ENV_FILE:-/etc/$SERVICE_NAME.env}"
SERVICE_FILE="${SERVICE_FILE:-/etc/systemd/system/$SERVICE_NAME.service}"

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
  PACKAGE_ARCHIVE=$PACKAGE_ARCHIVE

Examples:
  sudo API_KEY='replace-with-secret' PORT=5080 $0 install-service
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

  command -v dotnet 2>/dev/null || true
}

dotnet_service_path() {
  local resolved
  resolved="$(dotnet_cmd)"
  [ -n "$resolved" ] || fail "dotnet was not found; run install-deps first"
  readlink -f "$resolved"
}

service_env() {
  if [ -f "$ENV_FILE" ]; then
    # shellcheck disable=SC1090
    set -a && . "$ENV_FILE" && set +a
  fi
}

cmd_show_config() {
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
CONFIG
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
  apt-get install -y ca-certificates curl gzip tar wget gpg

  if ! have dotnet; then
    log "Installing Microsoft package feed"
    local repo_deb="/tmp/packages-microsoft-prod.deb"
    wget -O "$repo_deb" "https://packages.microsoft.com/config/${ID}/${VERSION_ID}/packages-microsoft-prod.deb"
    dpkg -i "$repo_deb"
    apt-get update
  fi

  log "Installing .NET 10 SDK"
  apt-get install -y dotnet-sdk-10.0
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
  log "Writing $ENV_FILE"
  cat >"$ENV_FILE" <<ENV
ASPNETCORE_ENVIRONMENT=Production
MUSICDECRYPTO_MANAGE_BIND_HOST=$BIND_HOST
MUSICDECRYPTO_MANAGE_PORT=$PORT
Kestrel__Endpoints__Http__Url=http://$BIND_HOST:$PORT
MusicDecrypto__StorageRoot=$DATA_DIR
MusicDecrypto__TempRoot=$TEMP_DIR
MusicDecrypto__DecryptoExecutablePath=$PACKAGE_DIR/musicdecrypto
MusicDecrypto__ApiKey=$API_KEY
MusicDecrypto__ForceOverwrite=$FORCE_OVERWRITE
MusicDecrypto__ExtensiveDetection=$EXTENSIVE_DETECTION
ENV
  chmod 600 "$ENV_FILE"
}

write_service_file() {
  local dotnet_path
  dotnet_path="$(dotnet_service_path)"

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
ExecStart=$dotnet_path $PUBLISH_DIR/MusicDecrypto.Backend.dll
Restart=always
RestartSec=5

NoNewPrivileges=true
PrivateTmp=true
ProtectSystem=full
ReadWritePaths=$DATA_DIR $TEMP_DIR

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
