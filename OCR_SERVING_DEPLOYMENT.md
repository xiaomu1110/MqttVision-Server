# PaddleOCR Serving Deployment

MqttVision.Server calls PaddleOCR as an external HTTP service. The C# process
does not load PaddleOCR or Paddle Inference directly.

## Recommended Route

Start with PaddleX basic serving to validate OCR quality and request/response
shape. Use PaddleX high-stability serving for production after the basic route
is stable. The official PaddleX guide states that high-stability serving is
based on NVIDIA Triton Inference Server and currently supports Linux only, so
deploy that component on a Linux server or Linux VM/container host.

## Basic Serving

Install the serving dependencies in the Python environment that has PaddleX:

```powershell
paddlex --install serving
```

Start the OCR service:

```powershell
paddlex --serve --pipeline OCR --host 127.0.0.1 --port 8080
```

The default basic endpoint used by MqttVision is:

```text
http://127.0.0.1:8080/ocr
```

## MqttVision Configuration

Set these values under `MqttVision:Processing`:

```json
{
  "PaddleOcrEnabled": true,
  "PaddleOcrDeploymentMode": "basic-serving",
  "PaddleOcrServiceUrl": "http://127.0.0.1:8080/ocr",
  "PaddleOcrVisualize": false,
  "PaddleOcrFileType": 1,
  "PaddleOcrUseDocOrientationClassify": false,
  "PaddleOcrUseDocUnwarping": false,
  "PaddleOcrUseTextlineOrientation": true,
  "PaddleOcrMinimumTextScore": 0.5,
  "PaddleOcrTimeoutSeconds": 30
}
```

`MqttVision.Server` sends each YOLO crop as a base64 image to the OCR service
and reads PaddleOCR fields such as `rec_texts`, `rec_scores`, `text`,
`confidence`, and HPS-wrapped `outputs[].data[]` responses. Text lines below
`PaddleOcrMinimumTextScore` are ignored when PaddleOCR returns per-line scores.

For high-stability serving, change:

```json
{
  "PaddleOcrDeploymentMode": "high-stability-serving",
  "PaddleOcrServiceUrl": "http://<ocr-host>:8000/v2/models/ocr/infer"
}
```

## High-Stability Serving Notes

1. Download the `paddlex_hps_OCR_sdk.tar.gz` package from the PaddleX
   high-stability serving SDK table.
2. Edit the SDK `server/pipeline_config.yaml` to point to the desired OCR model
   directories and runtime settings.
3. Tune the Triton `server/model_repo/<endpoint>/config*.pbtxt` files for CPU or
   GPU instance counts.
4. Start the OCR service from the SDK server directory according to the SDK
   README.
5. Point `PaddleOcrServiceUrl` to the exposed HPS endpoint, usually
   `http://<ocr-host>:8000/v2/models/ocr/infer`.

Keep the OCR service internal to the server network. The phone app should keep
talking only to MqttVision.Server.
