# 当前图标审阅导出器

独立工具调用生产程序集中的 `SemanticIcon` 和 `ActionIconMotion`，在屏幕外的 WPF 渲染源中播放有限动画，输出分类 PNG、GIF、透明逐帧 PNG、原始 SVG/ICO 和绘制源码。

先构建主项目，再运行（从项目目录执行）：

```powershell
& .\tools\IconReviewExporter\Export-IconReview.ps1
```

脚本检查当前主项目 Release 程序集版本一致后，构建工具、实际渲染并验证 GIF，最后在项目 `bin` 生成同名 ZIP；仅导出自身图标包，不复制到桌面或修改安装目录。

输出固定在项目 `bin\IconReview-2026-10-11`，可双击 `index.html` 或查看总览 PNG。`manifest.json` 记录分类计数与所有字体码点的来源。字体中没有的范围标记不会作为方框图标输出，详见 `非字体字形说明.txt`。

工具不读取用户配置、密钥、会话、媒体封面、通知内容或窗口截屏。媒体/助手真实窗口入岛与出岛是交互动画，不属于固定图标素材；不能用用户真实窗口截图作为图标导出来源。

`Program.cs.txt` 为显式编译输入，扩展名避免被上级应用项目默认递归编译。导出工具及其缓存不进入程序发布内容。
