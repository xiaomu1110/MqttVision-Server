# Docker 部署

本分支提供 MqttVision Server 的可运行 Docker 镜像。镜像只包含服务端程序和 YOLO 模型；EMQX、PaddleOCR 和持久化数据独立管理。

## 构建与启动

```bash
cp .env.example .env
mkdir -p /var/lib/mqttvision/runtime \
         /var/lib/mqttvision/Configuration \
         /var/lib/mqttvision/config
cp config/mqttvision.yaml /var/lib/mqttvision/config/mqttvision.yaml

# 按实际服务器地址修改 .env 中的 PUBLIC_BASE_URL、MQTT 和 PaddleOCR 配置。
docker compose build --pull server
docker compose up -d server
```

检查状态：

```bash
docker compose ps
docker compose logs --follow server
curl http://127.0.0.1:5080/api/health
```

容器内的服务使用非 root 用户（UID/GID 10001）。宿主机持久化目录需要允许该用户读写：

```bash
chown -R 10001:10001 /var/lib/mqttvision
```

## 依赖连接

- EMQX：默认使用 `host.docker.internal:1883`，因为当前服务器的 EMQX 已经在宿主机端口 1883 上运行。
- PaddleOCR：通过 `PADDLE_OCR_SERVICE_URL` 配置。若 OCR 作为同一个 Compose 项目中的服务，使用 `http://paddleocr:8080/ocr`。
- 手机端访问地址：将 `PUBLIC_BASE_URL` 设置为手机可访问的服务器内网地址或 HTTPS 域名。

## 数据和升级

- `/var/lib/mqttvision/runtime`：上传图片、归档、日志、Data Protection 密钥以及管理员上传的目标检测模型（`runtime/models/*.onnx`）。
- `/var/lib/mqttvision/Configuration`：CAD 导入生成的柜体配置。
- `/var/lib/mqttvision/config/mqttvision.yaml`：生产运行配置，管理员热更新可能会写入此目录。
- 升级前备份上述目录；升级只替换镜像，不删除数据目录。

管理员登录后台的“系统配置 → 检测流程”后，可以上传新的 `.onnx` 文件；上传完成后从“目标检测模型文件”下拉菜单选择并保存配置。模型会写入上述 `runtime/models` 目录，镜像升级不会删除已上传模型。

## 生产注意事项

当前 Compose 文件先暴露 5080 便于联调。正式生产建议在前面增加 Nginx/HTTPS，并限制 EMQX 1883 和管理端的来源网络。
