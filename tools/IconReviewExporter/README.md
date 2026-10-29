# 图标导出工具

## 增量导出

`--remaining` 模式从当前生产程序集渲染，按排除清单筛选图标；生成带编号的网页、总览、CSV、透明 256px PNG、绘制轮廓 SVG 和动态图标帧。`manifest.json` 同时记录完整排除清单和生产程序集散列。

可直接引用与项目版本一致的发布目录，避免为了导出图标重建或启动主程序：

```powershell
dotnet build tools/IconReviewExporter/IconReviewExporter.csproj -c Release -p:IconSourceDirectory="完整的当前发布目录" -o bin/IconReviewExporterCurrent
& .\bin\IconReviewExporterCurrent\AlphaIsland.IconReviewExporter.exe $PWD --remaining
```

输出为全新的 `bin/IconReview-日期时间-v版本-remaining` 目录，不覆盖旧审阅结果。工具核验 PNG 非空、SVG 路径可解析、GIF 帧完整及排除规则；桌面交付和 ZIP 由调用方在核验后完成。

## 全量导出

独立工具调用生产程序集中的 `SemanticIcon` 和 `ActionIconMotion`，在屏幕外的 WPF 渲染源中播放有限动画，输出分类 PNG、GIF、透明逐帧 PNG、原始 SVG/ICO 和绘制源码。

先构建主项目，再运行（从项目目录执行）：

```powershell
& .\tools\IconReviewExporter\Export-IconReview.ps1
```

脚本检查当前主项目 Release 程序集版本一致后，构建工具、实际渲染并验证 GIF，最后在项目 `bin` 生成同名 ZIP；仅导出自身图标包，不复制到桌面或修改安装目录。

输出固定在项目 `bin\IconReview-2026-10-11`，可双击 `index.html` 或查看总览 PNG。`manifest.json` 记录分类计数与所有字体码点的来源。字体中没有的范围标记不会作为方框图标输出，详见 `非字体字形说明.txt`。

工具不读取用户配置、密钥、会话、媒体封面、通知内容或窗口截屏。媒体/助手真实窗口入岛与出岛是交互动画，不属于固定图标素材；不能用用户真实窗口截图作为图标导出来源。

`Program.cs.txt` 为显式编译输入，扩展名避免被上级应用项目默认递归编译。导出工具及其缓存不进入程序发布内容。
