# 第三方代码与修改说明

本插件按 GNU GPL v3 发布，完整许可见 LICENSE。

`upstream/` 来自 Zhaoyi-ya / Dszsu 维护的 OPPO Pods Manager，提交
`f0b59b78bfbead8260b8b2a572176ada52aa2b4f`（1.1.10）：
https://github.com/Zhaoyi-ya/OppoPodsManager/tree/f0b59b78bfbead8260b8b2a572176ada52aa2b4f

保留其协议、型号数据、Windows 传输、解析与命令调度；排除 Avalonia 界面、Linux 后端、更新和原应用设置代码。
Alpha Island 的插件入口、WPF 页面、生命周期和状态适配位于 upstream 目录之外。
`Compatibility.cs` 为独立日志和中文标签适配，不依赖原应用全局设置。
对上游文件的具体修改记录在此文件中。

本地修改：PodManager/Parsing 增加由插件设置指定的型号覆盖；Win32BluetoothFinder 校正最低 Windows 版本标注；WindowsDeviceDiscovery 释放枚举打开的设备句柄；SPP/GATT 编码增加长度上限，SPP 解码拒绝不完整载荷。其余协议和型号文件保留原实现。
SppTransport 将 Winsock 初始化改为每实例拥有并在 Dispose 时平衡释放，避免反复启停可卸载插件导致引用计数累积。
0.1.1：PodState 与 PodManager/Parsing 增加各分路电量的更新时间；完整查询清空缺失分路，部分通知仅更新上报分路，拒绝超出 0–100 范围的电量值。断开连接清空更新时间。插件会话检查电量有效期与协议响应超时。

上游致谢协议研究项目 Leaf-lsgtky/OppoPods 和 1812z/OppoPods。
插件不包含原应用字体、图案或 EXE，也不要求安装原应用或 .NET 10。

插件完整对应源代码随发布包放在 source/ 内，并包含 LICENSE 和本说明。

## 0.1.3 新增品牌

upstream2/ 来源于同一 GPLv3 项目提交 96eddba02a666907e8583613cfc3461206808fba（2.0.0）。
https://github.com/Zhaoyi-ya/OppoPodsManager/tree/96eddba02a666907e8583613cfc3461206808fba

用户提供 EXE SHA256：2229ADA77B3294108C1DE864AD0F3EF1D19670D422C8AE4565882DB9AFA9F4B2。
该 DLL 包含以上提交标识；vivo 型号数据经结构化解析与该提交一致，导入资源保留用户 EXE 中的原始字节。OPPO 数据与当前 137 条记录结构化一致。

只导入 Windows 通信、核心状态模型、新增品牌协议、型号与文本数据；没有导入 Avalonia 界面、自动更新或原应用设置。为避免与保留的 OPPO 1.1.10 后端冲突，将新代码命名空间迁至 AlphaIsland.EarbudsV2，并调整嵌入资源名。
移植修改：异步方法中的 Span 局部变量改用字节数组以兼容 C# 12；MBB 自检增加空帧保护；独立中文文本适配，不写原程序日志。电量快照增加单独更新时间，其他功能更新不能刷新电量有效期。RFCOMM 和 SDP 改为有所有权的 Winsock 引用，连接取消后也收集迟到连接。
新适配、WPF 布局及生命周期代码位于 upstream2 外部的 MultiBrand* 文件中；完整对应源码随插件 ZIP 与安装包 licenses/OppoPods-corresponding-source.zip 分发，原 GPLv3 许可保持。
