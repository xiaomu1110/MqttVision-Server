# CAD 批量导入

管理员登录后打开 `/admin/cabinets`（后台导航中的“CAD 导入”），选择一批 `.dwg` 或 `.dxf` 文件并点击“开始导入”。每个文件会单独生成柜体配置，后台最多同时解析 3 个文件。

每个批次保存到 `runtime/cad-imports/yyyy/MM/dd/<batch-id>/`，包含：

- `source/`：上传的原始 CAD 文件；
- `parsed/*_extracted_text.json`：CAD 文本提取结果；
- `parsed/*_relations.json`：端子排、端子号、左右线号管、2D 辅助字段和警告；
- `backup/`：导入前同名柜体配置的备份；
- `batch.json`：批次和每个文件的状态、进度、错误及产物路径。

生成的独立柜体配置写入 `MqttVision:Processing:CabinetConfigurationRoot`，默认是 `Configuration/<cabinet-id>.json`。页面会实时显示状态、产物绝对路径和“端子号–线号管号”预览。

后台接口：

- `GET /api/admin/cad-imports/`：批次列表；
- `GET /api/admin/cad-imports/{batchId}`：批次详情；
- `POST /api/admin/cad-imports/`：使用 `multipart/form-data`，字段可重复，上传多个 CAD 文件。

当前解析器支持带端子/线号管的 1D、2D 列表；3D 和无法识别的中间列会保留警告，不会静默丢失证据。CAD 坐标只用于同一图纸中的表格分组和审计，不与照片坐标建立映射。

检测任务执行时，服务端会读取配置目录中的全部柜体 JSON，建立线号管编号索引。图片中的所有线号管 OCR 会参与中心向外的候选投票，自动输出柜体、端子排、匹配置信度和每个 OCR 结果的命中证据；未命中或冲突时会保留警告，并降级到任务携带的柜体配置（如有）。
