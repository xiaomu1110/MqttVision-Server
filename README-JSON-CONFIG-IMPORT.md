# JSON 柜体配置导入

后台 `/admin/cabinets` 现在只接受 JSON 配置文件，不再读取 DWG、DXF 或 PDF。一次可选择多个文件；每个文件独立创建柜体配置，后台最多同时处理三个文件。

## 支持的 JSON 结构

推荐使用线号管关系映射格式：

```json
{
  "101/QF3-4": { "terminal_block": "CD", "terminal": "1" },
  "101/YK-1": { "terminal_block": "CD", "terminal": "1" },
  "115/4XS-4": { "terminal_block": "CD", "terminal": "5" }
}
```

键是完整线号管编号，值中的 `terminal_block` 是端子排编号，`terminal` 只接受纯数字或数字加小写字母（例如 `1`、`1a`）。同一端子可以有多个线号管关系；解析器会保留全部编号，并对数字序列中缺失的端子补充“设计空端子”。

系统也兼容已生成的 `CabinetConfiguration` JSON。导入后会统一规范为服务端使用的柜体配置格式。

## 导入产物

每批数据保存到 `runtime/configuration-imports/yyyy/MM/dd/<batch-id>/`：

- `source/`：原始 JSON 文件；
- `backup/`：覆盖同名柜体配置前的备份；
- `batch.json`：批次、进度、警告和端子预览。

生成的柜体配置写入 `Processing.CabinetConfigurationRoot`，默认是 `Configuration/`。JSON 导入的原始文件、生成配置和备份路径都会显示在后台页面中。

## API

- `GET /api/admin/json-config-imports/`：批次列表；
- `GET /api/admin/json-config-imports/{batchId}`：批次详情；
- `POST /api/admin/json-config-imports/`：使用 `multipart/form-data` 上传一个或多个 `.json` 文件。

导入配置由 `MqttVision:JsonImport` 控制，扩展名固定为 `.json`，并发数限制为 1–3。
