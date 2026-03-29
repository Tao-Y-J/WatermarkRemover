# WatermarkRemover

基于 `C# + WPF MVVM + OpenCvSharp + ONNX Runtime` 的手动遮罩去水印工具。

## 当前实现

- WPF 桌面界面，主流程采用 MVVM 组织
- 支持加载原图、选择 LaMa 风格 ONNX 模型
- 左侧内置手动画笔遮罩编辑器，支持橡皮擦与画笔尺寸调节
- 处理链路包含：
  - 原图读取
  - 遮罩导出
  - 输入尺寸对齐
  - ONNX Runtime 推理
  - 输出恢复到原图尺寸
  - 结果预览与保存

## 目录结构

```text
src/WatermarkRemover.App/
  Controls/       遮罩编辑控件
  Models/         推理请求模型
  Services/       文件、图像、ONNX 推理服务
  ViewModels/     主界面 ViewModel
```

## 运行方式

1. 准备一个支持 `image + mask` 输入的 LaMa ONNX 模型。
2. 执行：

```powershell
dotnet build .\WatermarkRemover.slnx
dotnet run --project .\src\WatermarkRemover.App\WatermarkRemover.App.csproj
```

3. 在界面中：
   - 加载待处理图片
   - 选择 ONNX 模型
   - 在左侧直接涂抹水印区域
   - 点击“开始去水印”
   - 预览并保存结果

## 模型约定

- 优先识别输入名包含 `image` / `mask` 的 ONNX 模型
- 如果模型输入名不标准，会退回按通道数猜测：
  - 3 通道视为图像输入
  - 1 通道视为 Mask 输入
- 输出默认取 3 通道图像张量

## 说明

- 当前版本偏向单图桌面工作流，不含批处理和模型管理模块。
- 如果后续要继续扩展，建议优先补：
  - 模型配置面板
  - 批量任务队列
  - 遮罩羽化与边缘融合
  - GPU Provider 切换
