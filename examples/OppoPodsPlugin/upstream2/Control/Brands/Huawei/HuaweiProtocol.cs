namespace AlphaIsland.EarbudsV2.Control.Brands.Huawei;

// 华为耳机私有协议变体：
// - Legacy：老 FreeBuds / OpenFreebuds 线制（5A 00 + LE 长度 + XMODEM CRC），现有全部型号默认走此；
// - Mbb：新 HarmonyOS 智慧音频线制（5A + BE 长度 + CTRL + CCITT CRC），由逆向 base.apk 还原。
// 两者命令号共用（service 43 == 老 0x2B），故切换协议仅需替换帧编解码器，业务命令常量不变。
public enum HuaweiProtocol
{
    Legacy,
    Mbb,
}
