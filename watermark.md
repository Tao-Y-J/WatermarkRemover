# 🧠 C# AI 去水印工具技术方案（生产级）

## 一、项目简介

本项目基于 **C# + ONNX Runtime + AI图像修复模型（LaMa）**
实现一个高质量去水印工具。

## 二、技术选型

-   UI：WPF / WinUI
-   图像处理：OpenCvSharp
-   AI推理：ONNX Runtime
-   模型：LaMa

## 三、核心流程

原图 → Mask → 预处理 → 推理 → 后处理 → 输出

## 四、关键代码示例

``` csharp
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
```

## 五、总结

推荐方案： C# + ONNX Runtime + LaMa + 手动Mask
