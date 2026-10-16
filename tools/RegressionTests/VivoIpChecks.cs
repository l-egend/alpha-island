using System.IO;
using System.Text.Json;
using AlphaIsland.Plugins.VivoOffice;

internal static partial class Program
{
    private static void VivoIpChecks(string root)
    {
        string folder=Path.Combine(root, "vivo-ip"); Directory.CreateDirectory(folder);
        string history=Path.Combine(folder, "historyPhone.json"), config=Path.Combine(folder, "config.json");
        bool running=true;
        var reader=new VivoConnectionReader(history,config,()=>running);
        var recent=new { mobileDeviceName="手机 A", device_id="a", connectFlag="WLAN", ip="192.168.10.42", pcIp="192.168.10.1" };
        void Write(object[]? phone=null) => File.WriteAllText(history,JsonSerializer.Serialize(new {phone=phone??Array.Empty<object>(),last_time_phone=recent}));
        Write(); var sample=reader.Read();
        Check(sample.IpAddress=="192.168.10.42" && !sample.IsCurrent && !sample.IsIpCurrent,
            "已断开设备的内网地址来自最近记录，明确保留缓存标记");
        Write(new object[] {new {mobileDeviceName="手机 A",device_id="a",connectFlag="WLAN",ip="192.168.10.43"}}); sample=reader.Read();
        Check(sample.IpAddress=="192.168.10.43" && sample.IsCurrent && sample.IsIpCurrent,"当前连接地址优先于同一设备的历史地址");
        Write(new object[] {new {mobileDeviceName="手机 B",device_id="b",connectFlag="WLAN"}}); sample=reader.Read();
        Check(sample.DeviceName=="手机 B" && sample.IpAddress=="","不同设备不会混入上一台手机的 IP");
        Write(new object[] {new {mobileDeviceName="手机 A",device_id="a",connectFlag="WLAN"}}); sample=reader.Read();
        Check(sample.IpAddress=="192.168.10.42" && sample.IsCurrent && !sample.IsIpCurrent,
            "当前条目缺少地址时可展示同设备最近地址，保留历史地址标记");
        File.WriteAllText(config,"{\"getConnectedDeviceInfoList\":[{\"deviceId\":\"a\",\"ip\":\"10.20.30.40\"}]}"); sample=reader.Read();
        Check(sample.IsIpCurrent && sample.IpAddress=="10.20.30.40","当前设备信息列表可补全同设备的当前 IP");
        Write(); sample=reader.Read();
        Check(sample.IsCurrent && sample.IsIpCurrent && sample.IpAddress=="10.20.30.40","历史 phone 列表为空时仍可读取已确认在线设备的当前 IP");
        File.WriteAllText(config,"{\"getConnectedDeviceIdList\":[\"a\"],\"getConnectedDeviceInfoList\":[{\"deviceId\":\"b\",\"ip\":\"10.99.1.2\"}]}"); sample=reader.Read();
        Check(sample.IsCurrent && !sample.IsIpCurrent && sample.IpAddress=="192.168.10.42","其他设备的配置 IP 不会串入已连接的当前手机");
        running=false; sample=reader.Read();
        Check(!sample.IsCurrent && !sample.IsIpCurrent && sample.IpAddress=="192.168.10.42","套件退出后仅显示该设备最近记录的地址");
        running=true; File.Delete(config);
        foreach (var (value,expected) in new[] {
            ("10.1.2.3","10.1.2.3"),("172.16.0.4","172.16.0.4"),("172.31.255.4","172.31.255.4"),
            ("169.254.1.4","169.254.1.4"),("fe80::1234","fe80::1234"),("fdab::42","fdab::42"),
            ("::ffff:192.168.1.4","192.168.1.4"),("8.8.8.8",""),("172.32.0.4",""),("127.0.0.1",""),
            ("0.0.0.0",""),("2001:db8::2",""),("not-an-ip",""),("","") })
        {
            Write(new object[] {new {mobileDeviceName="手机 B",device_id="b",ip=value}}); sample=reader.Read();
            Check(sample.IpAddress==expected,"内网地址解析与无效地址过滤: "+value);
        }
        Write(new object[] {new {mobileDeviceName="手机 B",device_id="b",pcIp="192.168.1.1"}});
        Check(reader.Read().IpAddress=="","电脑自身 pcIp 不会被当成手机 IP");
        Write(new object[] {new {mobileDeviceName="手机 A",device_id="a",ip="10.1.2.3"}}); reader.Read();
        File.WriteAllText(history,"{partial"); sample=reader.Read();
        Check(sample.IpAddress=="10.1.2.3" && !sample.IsCurrent && !sample.IsIpCurrent,"记录写入中保留地址但撤销实时地址标记");
        File.Delete(history);
        Check(reader.Read().IpAddress=="","连接记录删除后清空 IP，不残留上次设备地址");
        var events=new VivoConnectionEvents(); var now=DateTimeOffset.UtcNow;
        var current=new VivoConnection("手机 A","Wi-Fi",now,true,IpAddress:"10.1.2.3",IsIpCurrent:true);
        events.Observe(current,now);
        Check(!events.Observe(current with { IpAddress="10.1.2.4" },now),"仅 IP 刷新不会制造手机重连提醒");
    }
}