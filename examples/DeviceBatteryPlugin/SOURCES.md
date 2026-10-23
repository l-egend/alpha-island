# 协议与接口来源

- [2.4G-battery-viewer](https://github.com/yszyszysz233/2.4G-battery-viewer)：已验证的 ATK/VXE 与 DARKRIM 设备标识、只读查询字段、CRC 与 TLV 帧格式。
- [Logitech HID++ 文档](https://github.com/Logitech/cpg-docs/tree/master/hidpp20)：特性发现与 HID++ 查询格式。
- [Linux HID++ 驱动](https://github.com/torvalds/linux/blob/master/drivers/hid/hid-logitech-hidpp.c)：电池特性字段及电压 / SOC 数值参考。本实现只使用稀疏数值锚点进行独立插值，明确标示估算。
- [SDL Flydigi 驱动](https://github.com/libsdl-org/SDL/blob/main/src/joystick/hidapi/SDL_hidapi_flydigi.c)：经典 `05 EC` 身份查询及 Vader 4 Pro 型号标识。
- [Flydigi 经典协议记录](https://github.com/uiltonlopes/flydigi-space-station-mac/blob/main/docs/protocol.md)：DInput 报文布局与接口标识。
- [OpenFlydigi 电池字段记录](https://github.com/mkaliaha/openflydigi/blob/main/docs/device-settings.md)：f4 型号专属档位映射；不套用其他型号的 20% 电量比例。
- [Microsoft RawGameController.TryGetBatteryReport](https://learn.microsoft.com/en-us/uwp/api/windows.gaming.input.rawgamecontroller.trygetbatteryreport)：手柄电池报告。
- [Microsoft XInputGetBatteryInformation](https://learn.microsoft.com/en-us/windows/win32/api/xinput/nf-xinput-xinputgetbatteryinformation)：手柄电池类型与档位。

这些链接用于记录协议事实与系统 API 来源。本插件未复制或打包参考仓库的程序、图片、提示词或安装脚本。

- [迈从 M HUB 官方网页驱动](https://www.mchose.com.cn/#/connectDevice)及公开构建脚本 `assets/purify.es-CjCZz1i7.js`：K5 Ultra 的型号映射、`0x4D` 输出报文、`0x0900` 只读设备信息指令、XOR 校验和电量 / 充电字段。2026-10-18 本机 K5 Ultra 接收器验证，插件只实现上述读数，不执行网页代码或改写鼠标配置。
