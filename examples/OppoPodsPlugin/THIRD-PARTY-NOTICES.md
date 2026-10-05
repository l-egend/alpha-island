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
