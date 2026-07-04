# MqttVision.Server

电气柜端子接线核验系统的云端服务端。通过 MQTT 接收手机端提交的检测任务，运行 YOLO ONNX 目标检测 + PaddleOCR 文字识别，输出检测报告和可视化复核图。

> 此仓库为独立拆分后的服务端项目，配套手机端项目见 [MqttVision-Mobile](https://github.com/xiaomu1110/MqttVision-Mobile)。

## 技术栈

- .NET 10 / ASP.NET Core
- MQTTnet
- Microsoft.ML.OnnxRuntime (YOLO ONNX)
- SixLabors.ImageSharp
- YamlDotNet
- PaddleOCR / PaddleX Serving

## 快速开始

```powershell
# 还原依赖
dotnet restore

# 启动服务
dotnet run
```

启动后访问：

- `http://localhost:5080/ops` — 运维仪表盘
- `http://localhost:5080/api/health` — 健康检查

## 配置文件

编辑 `config/mqttvision.yaml`，或通过 `MQTTVISION_CONFIG` 环境变量指定路径。

最低配置：

```yaml
MqttVision:
  PublicBaseUrl: "http://<server-lan-ip>:5080"
  Mqtt:
    BrokerHost: "<mqtt-host>"
    BrokerPort: 1883
  Processing:
    YoloOnnxModelPath: "Models/yolo-best.onnx"
    PaddleOcrServiceUrl: "http://127.0.0.1:8080/ocr"

Client:
  ApiBaseAddress: "http://<server-lan-ip>:5080/"
```

## 发布的仓库结构和说明

```text
.
├── Api/                        # HTTP REST API 端点
├── Application/                # 检测任务队列与处理流水线
├── Components/                 # 运维页面 Blazor 组件
├── Configuration/              # 配置模型与柜体配置示例
├── Contracts/                  # 消息与 API 合约
├── Domain/                     # 检测、配对、OCR 领域模型
├── Infrastructure/             # MQTT、存储、视觉识别实现
├── Models/                     # YOLO ONNX 模型文件
├── Operations/                 # 运维状态快照
├── Properties/                 # launchSettings
├── config/
│   └── mqttvision.yaml         # 运行时配置
├── appsettings.json            # ASP.NET Core 默认配置
├── OCR_SERVING_DEPLOYMENT.md   # PaddleOCR 服务化部署指南
└── runtime/                    # 运行时产物（本地生成，不纳入 Git）
```

## 环境要求

- .NET SDK 10.0+
- 可访问的 MQTT Broker
- PaddleOCR Serving（可选，令 `PaddleOcrEnabled: false` 可跳过）

## 发布

```powershell
dotnet publish -c Release -o ./publish
```