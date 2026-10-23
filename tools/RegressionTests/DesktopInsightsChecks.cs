using alpha_island.Core;

internal static partial class Program
{
    private static void DesktopInsightsChecks()
    {
        var zone=TimeZoneInfo.FindSystemTimeZoneById("China Standard Time");
        var start=new DateTimeOffset(2026,9,28,9,30,0,TimeSpan.FromHours(8));
        var learner=new HabitLearner(zone);
        long stamp=1000;
        for(int day=0;day<5;day++)
        {
            var time=start.AddDays(day);
            for(int i=0;i<=20;i++){learner.Observe(new(time.AddSeconds(i*15),stamp,true,true,false,false,false,0));stamp+=15000;}
            learner.Observe(new(time.AddMinutes(10),stamp,false,false,false,false,false,600));stamp+=24*3600000;
        }
        var now=start.AddDays(7);
        var suggestions=learner.Suggestions(now,25,75);
        var work=suggestions.Single(value=>value.Id=="work:weekday");
        Check(work.ReminderText.Contains("09:25")&&learner.ObservedDays==5,"五个稳定观察日后建议提前五分钟提醒专注");
        Check(!System.Text.Json.JsonSerializer.Serialize(learner.Snapshot()).Contains("ProcessName"),"学习持久化只含时间桶、类别与时长，没有应用或窗口内容");
        Check(learner.CanOffer(work.Id,now),"达到有效样本要求后才可主动提示");
        learner.Offered(work.Id,now);Check(!learner.CanOffer(work.Id,now.AddHours(1)),"同一天不会重复提出学习建议");
        learner.Respond(work.Id,false,now);Check(learner.Suggestions(now.AddDays(1),25,75).All(value=>value.Id!=work.Id),"拒绝后十四天内不重复建议");
        var expired=new HabitLearner(zone);expired.Restore(learner.Snapshot(),now);
        Check(expired.Suggestions(now.AddDays(40),25,75).Length==0&&expired.ObservedDays==0,"长期关闭学习后查看建议也会剔除过期观察，不使用28天以前的规律");
        var recovered=new HabitLearner(zone);recovered.Restore(learner.Snapshot(),now.AddDays(1));
        Check(recovered.ObservedDays==5&&recovered.Suggestions(now.AddDays(1),25,75).All(value=>value.Id!=work.Id),"重启保留学习记录和拒绝反馈");
        recovered.Clear();Check(recovered.ObservedDays==0&&recovered.Suggestions(now,25,75).Length==0,"清除学习后恢复到无样本状态");
        var shortVisit=new HabitLearner(zone);
        for(int day=0;day<7;day++)shortVisit.Observe(new(start.AddDays(day),1000+day*86400000L,true,true,false,false,false,0));
        Check(shortVisit.Suggestions(now,25,75).Length==0,"短暂打开应用或采样间断不会伪造稳定习惯");
        var health=new SystemHealthMonitor();
        for(int i=0;i<40;i++)health.Observe(new(now.AddSeconds(i*15),10,45,8L*1024*1024*1024));
        int warnings=0;
        for(int i=40;i<50;i++)warnings+=health.Observe(new(now.AddSeconds(i*15),30,45,8L*1024*1024*1024)).Length;
        Check(warnings==0,"CPU 从10到30的三倍变化不会仅因倍数报警");
        for(int i=50;i<60;i++)warnings+=health.Observe(new(now.AddSeconds(i*15),98,45,8L*1024*1024*1024)).Length;
        Check(warnings==1,"持续高 CPU 超过阈值只产生一次提醒");
        for(int i=60;i<80;i++)warnings+=health.Observe(new(now.AddSeconds(i*15),99,45,8L*1024*1024*1024)).Length;
        Check(warnings==1,"持续相同负载不会每次采样重复报警");
        health.Pause();warnings=0;
        for(int i=0;i<10;i++)warnings+=health.Observe(new(now.AddHours(1).AddSeconds(i*15),100,50,4L*1024*1024*1024,true)).Length;
        Check(warnings==0,"已识别游戏或预期高负载不会触发 CPU 异常提示");
        health.Reset();warnings=0;
        for(int i=0;i<8;i++)warnings+=health.Observe(new(now.AddHours(2).AddSeconds(i*15),20,97,100L*1024*1024)).Length;
        Check(warnings==1,"持续内存压力可以在没有历史基线时提醒");
        health.Reset();health.Observe(new(now,95,30,4L*1024*1024*1024));
        Check(health.Observe(new(now.AddHours(3),95,30,4L*1024*1024*1024)).Length==0,"睡眠和采样缺口不累计为连续异常");
        var process=new ProcessHealthSample(100,1234,"fixture-app",1,100*1048576L,0);
        health.Reset();warnings=0;
        for(int i=0;i<=20;i++)warnings+=health.ObserveProcesses([process with{PrivateBytes=(100+i*20)*1048576L}],now.AddSeconds(i*30),false).Length;
        Check(warnings==1,"跨八分钟的显著私有内存增长提示趋势并去重");
        health.Reset();warnings=0;
        for(int i=0;i<20;i++)warnings+=health.ObserveProcesses([process with{Started=i+1,PrivateBytes=(100+i*20)*1048576L}],now.AddSeconds(i*30),false).Length;
        Check(warnings==0,"同 PID 的新进程不会继承旧进程的内存增长趋势");
        health.Reset();warnings=0;
        for(int i=0;i<8;i++)warnings+=health.ObserveProcesses([process with{WrittenBytes=(ulong)i*30*60*1024*1024}],now.AddSeconds(i*30),false).Length;
        Check(warnings==1,"持续高进程写入经过多次有效采样才提示并去重");
        health.Reset();warnings=0;
        for(int i=0;i<20;i++)warnings+=health.ObserveProcesses([process with{PrivateBytes=(100+i*30)*1048576L}],now.AddSeconds(i*30),true).Length;
        warnings+=health.ObserveProcesses([process with{PrivateBytes=900*1048576L}],now.AddSeconds(600),false).Length;
        Check(warnings==0,"游戏等预期负载的进程增长不会被接到正常场景的趋势中");
        Check(new ScreenColor(255,0,0).Hex=="#FF0000"&&new ScreenColor(255,0,0).Hsl=="hsl(0, 100%, 50%)","取色 HEX 和 HSL 与标准红色一致");
        Check(new ScreenColor(128,128,128).Hsl.StartsWith("hsl(0, 0%")&&new ScreenColor(0,0,0).Rgb=="rgb(0, 0, 0)","灰度及黑色不产生无效色相或饱和度");
    }
}
