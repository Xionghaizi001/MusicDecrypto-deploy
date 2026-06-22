# MusicDecrypto 后端

这是一个 ASP.NET Core Minimal API 后端，用于接收加密音乐文件上传。上传使用 tus 断点续传协议，后端随后调用打包好的 `musicdecrypto` 命令行工具进行解锁处理。

## 技术栈

- .NET 10 LTS，`net10.0`
- ASP.NET Core Minimal API
- `tusdotnet`，用于 `/files` 断点续传上传
- 本地文件系统，用于保存上传文件、解锁产物和任务状态
- `deploy/package/musicdecrypto-linux-x64.tar.gz` 内含打包好的原生命令行工具，部署时会自动解压

## API

- `GET /healthz`：健康检查，不需要 API key
- `POST /files`：tus 创建上传任务
- `HEAD /files/{tus-id}`：tus 查询上传偏移量
- `PATCH /files/{tus-id}`：tus 上传分片
- `GET /api/jobs`：列出任务
- `GET /api/jobs/{id}`：获取单个任务
- `GET /api/jobs/{id}/download`：下载解锁产物
- `DELETE /api/jobs/{id}`：删除一个任务，以及对应的上传源文件和解锁产物
- `GET /update`：简单的浏览器更新文件上传页面
- `POST /update`：上传更新文件到 `UpdateRoot/{batchId}`
- `GET /update/batches`：列出已上传的更新批次
- `POST /update/{batchId}/apply`：校验并应用一个更新批次到 `UpdateApplyRoot`
- `DELETE /update/{batchId}`：删除一个已上传的更新批次

如果配置了 `MusicDecrypto:ApiKey`，访问 `/files` 和 `/api` 下的接口时必须带上以下任意一种认证方式：

- `X-Api-Key: <key>`
- `Authorization: Bearer <key>`

更新接口使用 `X-Update-Key`，值是配置的 API key 反转后的字符串。例如 API key 是 `abc123` 时，应发送 `X-Update-Key: 321cba`。浏览器更新页面里填写普通 API key 即可，页面会在请求前自动反转。

## 本地开发

安装 .NET 10 SDK，然后运行：

```bash
dotnet restore
dotnet run
```

默认本地地址是 `http://127.0.0.1:5080`。

命令行工具包以压缩包形式保存在 `deploy/package/musicdecrypto-linux-x64.tar.gz`。如果本地需要实际处理任务，先解压：

```bash
PACKAGE_DIR=package scripts/manage.sh extract-package
```

解压出来的根目录 `package/` 已被 git 忽略。源码管理只保留 `deploy/package/` 下的压缩包。

前端本地开发时，后端默认允许以下 CORS 来源：

- `http://localhost:5173`
- `http://127.0.0.1:5173`

可以用附带的 tus 兼容测试脚本上传文件：

```bash
chmod +x scripts/tus-upload.sh
scripts/tus-upload.sh http://127.0.0.1:5080 ./sample.ncm
```

开发辅助命令：

```bash
scripts/dev.sh server
scripts/dev.sh pack --git HEAD~1..HEAD -o /tmp/update.zip
scripts/dev.sh pack --files Program.cs src/Updates/UpdateEndpoints.cs -o /tmp/update.zip
```

`scripts/dev.sh server` 会使用 `PATH` 中正常安装的 `dotnet`。如果 dotnet 安装在自定义路径，可通过 `DOTNET_BIN=/path/to/dotnet` 指定。

更新包是一个 zip，根目录结构如下：

```text
musicdecrypto-update.json
files/<relative target paths>
```

清单格式是 `musicdecrypto.update.v1`，会列出每个目标路径、包内源路径、文件大小和 SHA-256。后端会先校验清单，再应用文件。

## VPS 部署

下面的命令默认面向 Ubuntu/Debian。默认安装方式使用：

- 应用目录：当前 checkout 出来的 `backend` 目录
- 服务用户：当前 `backend` 目录的所有者
- 数据目录：`/var/lib/musicdecrypto`
- tus 临时目录：`/var/tmp/musicdecrypto`
- 更新上传目录：`/var/lib/musicdecrypto/updates`
- 更新应用目录：应用目录
- 服务监听地址：`127.0.0.1:5080`

这样从仓库目录部署时，运行目录和 `/update` 的应用目标会保持一致。如果更喜欢传统的 `/opt` 安装方式，可以设置 `APP_DIR=/opt/musicdecrypto/backend`；除非单独覆盖，`PUBLISH_DIR`、`PACKAGE_DIR` 和 `APPLY_DIR` 都会跟随这个应用目录。

先检查环境：

```bash
scripts/manage.sh env-check
```

如果 VPS 上还没有 .NET SDK：

```bash
sudo scripts/manage.sh install-deps
```

`install-deps` 会先尝试使用 Microsoft apt 源中的 `dotnet-sdk-10.0`。如果当前系统或软件源中没有这个包，会回退到 Microsoft 官方安装脚本：

- 安装脚本：`https://dot.net/v1/dotnet-install.sh`
- 该脚本默认使用的 SDK 源：`https://builds.dotnet.microsoft.com/dotnet`
- 本项目默认安装目录：`/opt/dotnet`

需要时可以覆盖这些回退配置：

```bash
sudo \
  DOTNET_CHANNEL=10.0 \
  DOTNET_INSTALL_DIR=/opt/dotnet \
  DOTNET_INSTALL_SCRIPT_URL=https://dot.net/v1/dotnet-install.sh \
  scripts/manage.sh install-deps
```

安装并启动后端服务：

```bash
sudo API_KEY='replace-with-a-long-random-secret' scripts/manage.sh install-service
```

安装时可以用环境变量覆盖配置：

```bash
sudo \
  APP_DIR=/opt/musicdecrypto/backend \
  PORT=5081 \
  DATA_DIR=/srv/musicdecrypto/data \
  TEMP_DIR=/srv/musicdecrypto/tmp \
  UPDATE_DIR=/srv/musicdecrypto/updates \
  AUTO_DELETE_AFTER_DAYS=7 \
  ALLOWED_ORIGINS=https://your-frontend.example.com \
  API_KEY='replace-with-a-long-random-secret' \
  scripts/manage.sh install-service
```

日常操作：

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
sudo AUTO_DELETE_AFTER_DAYS=14 scripts/manage.sh configure
sudo ALLOWED_ORIGINS=https://app.example.com,https://admin.example.com scripts/manage.sh configure
sudo scripts/manage.sh reinstall-deps
sudo scripts/manage.sh uninstall
```

`configure` 会更新已安装的环境文件；如果路径或端口变化，也会重写 systemd unit，然后 reload systemd 并重启服务。命令行没有传入的变量会沿用当前已安装配置。

删除服务文件和数据：

```bash
sudo REMOVE_DATA=1 scripts/manage.sh uninstall
```

如果这个服务要公开访问，可以单独安装 Nginx 反向代理：

```bash
sudo cp deploy/nginx/musicdecrypto-backend.conf /etc/nginx/sites-available/musicdecrypto-backend.conf
sudo ln -s /etc/nginx/sites-available/musicdecrypto-backend.conf /etc/nginx/sites-enabled/musicdecrypto-backend.conf
sudo nginx -t
sudo systemctl reload nginx
```

启用 HTTPS 前，先编辑配置里的 `server_name example.com;`。

如果要把前端也放在后端应用目录旁边，并一步生成 Nginx 站点：

```bash
sudo \
  PORT=5081 \
  SERVER_NAME=dec.example.com \
  scripts/manage.sh web-deploy
```

默认前端输出目录是：

```text
$APP_DIR/frontend-dist
```

按传统默认安装路径计算，也就是：

```text
/opt/musicdecrypto/backend/frontend-dist
```

`web-deploy` 是 `install-web` 的别名。它会构建 `../frontend`，把 `dist/` 复制到 `FRONTEND_DIR`，写入 `NGINX_SITE_FILE`，测试 Nginx，reload Nginx，并把解析后的 Web 部署配置保存到 `/etc/musicdecrypto-web.env`。`SERVER_NAME` 只在首次 Web 部署或后续修改域名时需要提供。

当脚本以 root 运行时，前端依赖安装和构建会自动降权到普通用户执行，而不是直接用 root 跑 pnpm。默认构建用户是前端或后端项目目录的所有者。需要时可以手动覆盖：

```bash
sudo \
  FRONTEND_BUILD_USER=deploy \
  SERVER_NAME=dec.example.com \
  scripts/manage.sh web-deploy
```

如果没有提供 `SSL_CERTIFICATE` 和 `SSL_CERTIFICATE_KEY`，生成的站点只监听 HTTP。这适合 HTTPS 已由 1Panel 或外层反向代理处理的场景。

如果希望这个 Nginx 站点自己终止 HTTPS，请同时传入两个证书路径：

```bash
sudo \
  PORT=5081 \
  SERVER_NAME=dec.example.com \
  SSL_CERTIFICATE=/path/to/fullchain.pem \
  SSL_CERTIFICATE_KEY=/path/to/privkey.pem \
  scripts/manage.sh web-deploy
```

首次 Web 部署成功后，后续更新前端并按已保存配置重新生成 Nginx 站点，只需要：

```bash
sudo scripts/manage.sh update-web-deploy
```

如果要替换部分已保存的 Web 部署配置，只传变化的值即可：

```bash
sudo \
  SERVER_NAME=dec.example.com \
  SSL_CERTIFICATE=/path/to/fullchain.pem \
  SSL_CERTIFICATE_KEY=/path/to/privkey.pem \
  scripts/manage.sh update-web-deploy
```

如果要同时安装后端服务和前端站点：

```bash
sudo \
  API_KEY='replace-with-a-long-random-secret' \
  PORT=5081 \
  SERVER_NAME=dec.example.com \
  scripts/manage.sh install-all
```

生成的 Nginx 站点会从 `/` 提供前端页面，并把 `/api/`、`/files`、`/healthz` 和 `/update` 代理到本机后端。在这种同源部署下，前端里的后端地址设置可以留空。

## 备注

- tus 断点续传文件保存在 `TempRoot/tus`。
- 上传文件会复制到 `StorageRoot/uploads`。
- 解锁产物会写入 `StorageRoot/outputs/{jobId}`。
- 通过 `/update` 上传的文件会保存在 `UpdateRoot/{batchId}`。
- 应用更新批次时，后端会把清单列出的文件复制到 `UpdateApplyRoot`。默认情况下，`UpdateApplyRoot` 是 systemd 服务使用的应用目录。`/update` 的应用操作随后会在后台运行 `scripts/manage.sh publish` 并停止应用；配合生成的 systemd unit 中的 `Restart=always`，服务会用新发布的版本自动启动。
- 任务状态保存在 `StorageRoot/state/jobs.json`。
- 已完成和失败的任务会在 `AutoDeleteAfterDays` 天后自动删除。默认值是 `7`；如果想关闭自动删除，可执行 `AUTO_DELETE_AFTER_DAYS=0 scripts/manage.sh configure`。
- 当前 worker 一次只处理一个任务。这个策略对小 VPS 的 CPU 和磁盘压力更保守。
