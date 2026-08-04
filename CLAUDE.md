# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## 项目概览

MqttVision.Server 是电气柜端子接线核验系统的云端服务端(C# / .NET 10 / ASP.NET Core)。流程:手机端先经 HTTP 上传图片 → 经 MQTT 提交检测任务 → 服务端 YOLO ONNX 检测端子(类 0)/线号管(类 1)→ 几何配对 → 调用外部 PaddleOCR HTTP 服务识别线号文字 → 与柜体配置 JSON 比对 → 输出检测报告 + 可视化复核图,并通过 MQTT 回传进度与结果。

配套手机端仓库:https://github.com/xiaomu1110/MqttVision-Mobile

## 构建与运行

标准 .NET CLI,无自定义脚本:

```powershell
dotnet restore
dotnet run                          # 启动,Kestrel 监听 http://0.0.0.0:5080
dotnet publish -c Release -o ./publish
```

- 运维仪表盘:`http://localhost:5080/ops`(根 `/` 重定向到此)
- 健康检查:`http://localhost:5080/api/health`
- 静态文件 `/files` 映射到 `StorageRoot`(默认 `runtime`),供结果图/可视化图外链访问

### 启动前置

端到端运行需要三项外部依赖就绪(本机开发均已具备):
1. **.NET 10 SDK**(项目目标 `net10.0`,使用 `.slnx` 新解决方案格式、`required`/`init`/文件作用域 namespace 等现代 C# 特性)
2. **PaddleOCR serving**(本地 basic-serving `http://127.0.0.1:8080/ocr`,部署见 @OCR_SERVING_DEPLOYMENT.md)— 不是 NuGet 依赖,而是外部 HTTP 服务
3. **MQTT broker**(默认连 `config/mqttvision.yaml` 中的 BrokerHost)

开箱即用占位模式:`appsettings.json` 默认 `EnablePlaceholderPipeline: true`、`PaddleOcrEnabled: false`,即不加载 YOLO/不调 OCR 也能启动并产出占位结果。真实检测需切换到 `config/mqttvision.yaml` 配置。

## 配置加载

配置源加载顺序(见 `Configuration/MqttVisionYamlConfiguration.cs`),后者覆盖前者:
1. `appsettings.json`
2. `AddMqttVisionYaml` —— 解析 YAML 扁平化为键值注入 `IConfiguration`。搜索路径优先级:`MQTTVISION_CONFIG` 环境变量指定文件 → `config/mqttvision.yaml` → `config/mqttvision.local.yaml` → `mqttvision.yaml` → `mqttvision.local.yaml`,在 contentRoot/AppBaseDir/CurrentDirectory 及其所有父目录递归查找
3. 环境变量
4. 命令行参数

主运行时配置是 @config/mqttvision.yaml(含中文注释,完整覆盖所有选项)。强类型绑定见 `Configuration/MqttVisionServerOptions.cs`。修改配置项时同步更新这两处与 `appsettings.json` 的默认值。

## 关键架构

单项目,非 monorepo。分层目录职责:
- `Api/` —— Minimal API 端点(图片上传、结果查询、运维 summary/health)
- `Application/` —— 任务队列与检测流水线。`ChannelDetectionTaskQueue`(基于 `System.Threading.Channels` 无界队列,`SingleReader`)、`DetectionPipeline`(核心流水线)、`DetectionTaskProcessorService`(`BackgroundService` 消费队列)、`DetectionTaskWorkflow`(接收 MQTT 任务入队)
- `Components/` —— Blazor Server 运维页(交互式 Server 渲染,3 秒轮询刷新,无 SignalR/WebSocket)
- `Configuration/` —— 配置模型、YAML 配置源、存储路径初始化、柜体配置示例(`cabinet-dev.json`)
- `Contracts/` —— MQTT 消息与 API 合约(`DetectionTaskMessage`/`DetectionProgressMessage`/`DetectionResultMessage` 等)
- `Domain/` —— 领域模型与状态机(`DetectionTaskStatus`:Created→ImageReceived→MqttSubmitted→Queued→Processing→Completed/Failed)
- `Infrastructure/Mqtt/` —— `MqttTaskSubscriberService`(订阅 `mqttvision/+/+/task/submit`,QoS AtLeastOnce,V3.1.1,5 秒自愈重连)、`MqttDetectionResultPublisher`(独立客户端懒连接,发 progress/result)
- `Infrastructure/Storage/` —— 纯文件系统存储,无数据库
- `Infrastructure/Vision/` —— `YoloOnnxObjectDetector`(ONNX 推理 + letterbox + NMS,Lazy 加载 InferenceSession)、`PaddleOcrServingTextRecognizer`(HTTP 调用 PaddleOCR,解析 basic/high-stability 两种响应)、`CommandLineTextRecognizer`(备用实现,当前未在 DI 注册)
- `Operations/` —— 内存态运维快照(`OpsStateService`,无持久化)

入口:`Program.cs`(top-level statements,DI 注册 + 两个 HostedService + Blazor + 全开放 CORS)。

### 检测流水线(`DetectionPipeline.ProcessAsync`)

1. 定位上传图 → 若 `EnablePlaceholderPipeline` 则生成占位结果返回
2. YOLO 检测 → 按 class 裁剪到 `crops/terminals`、`crops/wire-tags`(JPEG q92)
3. `BuildPairs` 几何配对(水平/垂直间距、重叠率、边缘惩罚评分,分类 `confirmed`/`suspected-error`/`empty-terminal`)
4. `RunOcrAsync` 对每个裁剪图调 `ITextRecognizer`,存 `ocr-result.json`
5. `CompareWithConfigurationAsync` 加载 `{CabinetId}.json`,OCR 窗口搜索对齐,匹配/不匹配/无法识别(含重复声明抑制)
6. `SaveVisualSummaryAsync` 在原图上绘制检测框、配对连线、按比对结果着色(绿=matched/红=mismatch/橙=unrecognized)
7. 产出 `detection-result.json`(schemaVersion 1.0)、`detection-report.md`,返回各产物 URL

## 开发约定

- **分支与提交**:在特性分支上开发,通过 PR 合入 `main`。commit 用 Conventional Commits 前缀(`feat:`/`fix:`/`refactor:`/`docs:`/`chore:` 等)。
- **测试**:`MqttVision.Server.Tests` 项目(xUnit + FluentAssertions),已加入 `.slnx`。
  - 运行全部:`dotnet test MqttVision.Server.slnx`
  - 运行单个:`dotnet test --filter "FullyQualifiedName~PaddleOcrServingResponseParserTests"`
  - 测试聚焦纯逻辑(几何计算、OCR 响应解析),不起 HTTP/MQTT。`PaddleOcrServingTextRecognizer.ParseServingResponse` 为 `internal static` + `InternalsVisibleTo`,专供单测直接喂 JSON 验证 basic/high-stability 两种响应解析。改动解析逻辑后务必补对应测试。
  - 改动有运行时副作用的功能时,仍优先用 `/verify` 跑真实流程,单测只覆盖纯函数。
- **CI**:`.github/workflows/ci.yml` 在 push 到 `main` 与 PR 时运行 format 校验 + build + test。format 以 error 级违规为门禁;perf 类警告(CA18xx:LoggerMessage、`IReadOnlyList`→数组等)已在 `.editorconfig` 降为 suggestion,不阻塞——现有代码刻意保持抽象,不强行重构。
- **linting/格式化**:`.editorconfig` 定义全仓风格(4 空格、文件作用域 namespace warning、LF 行尾)。`dotnet format MqttVision.Server.slnx --verify-no-changes --severity error` 校验,去掉 `--verify-no-changes` 实际修复。行尾强制 LF(`.gitattributes`),提交前若 `dotnet format` 报 ENDOFLINE,运行 format 修复即可。
- **代码风格**:文件作用域 namespace、`sealed class`/`sealed record`、`init` 只读属性、中文注释与日志消息、`JsonSerializerDefaults.Web`。
- **环境变量**:改配置默认值时,同步 `config/mqttvision.yaml`、`Configuration/MqttVisionServerOptions.cs`、`appsettings.json` 三处。
- **`runtime/` 目录**(`uploads`/`archive`/`logs`)由 `ServerPathInitializer` 在启动时自动创建,被 `.gitignore` 忽略,运行时生成,勿提交。`MqttVision.Server.Tests/TestResults/` 同样被忽略。

## 注意

- `Models/yolo-best.onnx` 已纳入 Git(检测模型,类 0=端子、类 1=线号管在代码中硬编码)。
- CORS 全开放、MQTT 默认无认证、`/files` 静态文件无鉴权——当前为内网信任假设。
- `git` 不在系统 PATH,可执行文件在 `C:\Users\lyh20\AppData\Local\Programs\Git\cmd\git.exe`(GitHub Desktop 安装)。
