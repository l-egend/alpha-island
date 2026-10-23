using alpha_island.Core;
using alpha_island.Services;
using System.IO;

internal static partial class Program
{
    private static void PersonalReminderChecks()
    {
        var zone=TimeZoneInfo.FindSystemTimeZoneById("China Standard Time");
        var now=new DateTimeOffset(2026,10,19,9,0,0,TimeSpan.FromHours(8));
        ReminderDraft Parse(string text)=>ReminderTextParser.Parse(text,now,zone).Draft??throw new Exception("Cannot parse: "+text+" / "+ReminderTextParser.Parse(text,now,zone).Error);
        Check(Parse("30分钟后提醒我喝水").Due==now.AddMinutes(30),"中文相对分钟提醒在本机确定准确时间");
        Check(Parse("一小时三十分钟后提醒我站起来").Due==now.AddMinutes(90),"中文复合时长可解析");
        Check(Parse("半小时后提醒我喝水").Due==now.AddMinutes(30),"半小时表达可解析");
        Check(Parse("一个半小时后提醒我休息").Due==now.AddMinutes(90),"一个半小时表达可解析");
        Check(Parse("提醒我一个半小时后休息").Due==now.AddMinutes(90),"提醒开头的半小时表达也保持准确时长");
        Check(Parse("2天后提醒我复查").Due==now.AddDays(2),"天数提醒按本地日期推进");
        Check(TimeZoneInfo.ConvertTime(Parse("今晚12点提醒我休息").Due,zone).DateTime==new DateTime(2026,10,20,0,0,0),"晚上十二点不是当天中午");
        Check(Parse("提醒我30分钟后喝水").Title=="喝水","时间在提醒之后仍提取正确事项");
        Check(TimeZoneInfo.ConvertTime(Parse("明早8点提醒我开会").Due,zone).DateTime==new DateTime(2026,10,20,8,0,0),"明早日期与上午时段准确");
        Check(TimeZoneInfo.ConvertTime(Parse("下周一9点提醒我周会").Due,zone).DateTime==new DateTime(2026,10,26,9,0,0),"下周一不会落到本周");
        Check(Parse("每个工作日9:55提醒我专注").Weekdays.SequenceEqual(new[]{1,2,3,4,5}),"工作日重复规则包含五个工作日");
        Check(Parse("每天上午九点半提醒我休息").Weekdays.Length==7,"每天重复与中文半点可解析");
        Check(TimeZoneInfo.ConvertTime(Parse("每周五下午3点提醒我复盘").Due,zone).Hour==15,"每周和下午时间不会误解析成凌晨");
        Check(Parse("每隔20分钟提醒我眨眨眼").IntervalMinutes==20,"间隔重复保持原始周期");
        Check(Parse("每个周末10点提醒我休息").Weekdays.SequenceEqual(new[]{0,6}),"周末建议涵盖周六和周日");
        Check(TimeZoneInfo.ConvertTime(Parse("2026-10-20 12:30提醒我预约").Due,zone).DateTime==new DateTime(2026,10,20,12,30,0),"明确日期保留指定时分");
        Check(TimeZoneInfo.ConvertTime(Parse("2026-11-2 12:30提醒我预约").Due,zone).DateTime==new DateTime(2026,11,2,12,30,0),"单数字日不会与紧随的小时混成另一日期");
        foreach(string text in new[]{"今天8点提醒我开会","明天25点提醒我出门","0秒后提醒我喝水","晚上提醒我休息","2026-02-30 12:00提醒我吃饭","每隔10秒提醒我眨眼","999999999999分钟后提醒我喝水","一百百分钟后提醒我喝水"})
            Check(ReminderTextParser.Parse(text,now,zone) is {IsRequest:true,Draft:null},"无效或不明确时间不会创建提醒："+text);
        foreach(string text in new[]{"帮我写一个提醒程序","请解释30分钟后提醒我喝水这句话","写首诗","代码里面有每天9点提醒我开会的字符串","30分钟后提醒我喝水是什么意思"})
            Check(!ReminderTextParser.Parse(text,now,zone).IsRequest,"普通讨论不被当成实际创建请求："+text);
        Check(ReminderTextParser.IsListRequest("查看我的提醒列表")&&ReminderTextParser.CancellationTitle("取消喝水提醒")=="喝水","列表和精确事项取消在本机识别");
        var queue=new UserReminderQueue();
        var job=queue.Add(Parse("30分钟后提醒我喝水"),now);
        queue.Advance(now.AddMinutes(29));Check(queue.Pending.Length==0,"截止之前不提前提醒");
        queue.Advance(now.AddHours(1));var delivery=queue.Pending.Single();
        Check(delivery.Late&&!queue.Jobs.Single().Active,"睡眠或重启错过的一次性提醒补记一次");
        var restored=new UserReminderQueue();restored.Restore(queue.Snapshot(),now.AddHours(1));restored.Advance(now.AddHours(2));
        Check(restored.Pending.Length==1&&restored.Pending[0].Key==delivery.Key,"重启恢复未确认的发送记录使用同一幂等键");
        restored.Acknowledge(delivery.Key);var completed=restored.Snapshot();restored.Restore(completed,now.AddHours(2));restored.Advance(now.AddDays(1));
        Check(restored.Pending.Length==0,"已送达一次性提醒不会因再次启动重复触发");
        var repeating=queue.Add(Parse("每个工作日9:55提醒我专注"),now);
        queue.Advance(now.AddDays(4).AddHours(1));
        Check(TimeZoneInfo.ConvertTime(queue.Jobs.Single(value=>value.Id==repeating.Id).Due,zone).DayOfWeek==DayOfWeek.Monday,"错过多个工作日只补一条且正确跳过周末");
        Check(queue.Pending.Count(value=>value.JobId==repeating.Id)==1,"长时间离线不会补弹每个错过的重复周期");
        var interval=queue.Add(Parse("每隔20分钟提醒我休息"),now);queue.Advance(now.AddHours(3).AddMinutes(5));
        Check(queue.Jobs.Single(value=>value.Id==interval.Id).Due==now.AddHours(3).AddMinutes(20),"间隔提醒恢复后保持原始节拍，不从恢复时刻漂移");
        var snoozed=queue.Snooze(repeating.Id,10,now.AddDays(5));
        Check(snoozed.Weekdays.Length==0&&queue.Jobs.Single(value=>value.Id==repeating.Id).Weekdays.Length==5,"延后提醒创建独立一次性任务，不修改原有重复日程");
        Check(queue.Cancel(snoozed.Id)&&!queue.Cancel(snoozed.Id),"取消幂等且不会取消其他提醒");
        var limit=new UserReminderQueue();
        for(int i=0;i<100;i++)limit.Add(new("事项"+i,now.AddMinutes(i+1),zone.Id,[]),now);
        bool blocked=false;try{limit.Add(new("第101条",now.AddDays(1),zone.Id,[]),now);}catch(ArgumentException){blocked=true;}
        Check(blocked&&limit.Jobs.Count==100,"提醒任务有明确容量上限");
        var eastern=TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        var spring=new DateTimeOffset(2027,3,13,12,0,0,TimeSpan.Zero);
        var next=ReminderTextParser.NextLocal(eastern,2,30,[0,1,2,3,4,5,6],spring);
        Check(next.HasValue&&!eastern.IsInvalidTime(TimeZoneInfo.ConvertTime(next.Value,eastern).DateTime),"夏令时不存在时刻被跳过，不构造非法提醒");
        var fall=new DateTimeOffset(2026,11,1,5,30,0,TimeSpan.Zero);
        var fallQueue=new UserReminderQueue();fallQueue.Add(new("每日一次",fall,eastern.Id,[0,1,2,3,4,5,6]),fall.AddMinutes(-1));fallQueue.Advance(fall);
        Check(TimeZoneInfo.ConvertTime(fallQueue.Jobs[0].Due,eastern).Date>TimeZoneInfo.ConvertTime(fall,eastern).Date,"夏令时回拨的同一时刻不会让每日提醒触发两次");
        string folder=Path.Combine(Path.GetTempPath(),"alpha-reminder-persistence-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
        try
        {
            string file=Path.Combine(folder,"reminders.dat");
            using(var storage=new LocalStateFile<UserReminderState>(file,encrypted:true)){storage.Write(queue.Snapshot());Check(storage.Flush(TimeSpan.FromSeconds(3)),"提醒任务状态完成原子保存");}
            using(var storage=new LocalStateFile<UserReminderState>(file,encrypted:true))
            {var saved=storage.Read();Check(saved?.Jobs.Any(value=>value.Id==job.Id)==true,"加密提醒文件可在重新创建存储后恢复任务");}
            Check(!System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(file)).Contains("喝水"),"提醒内容不会明文写入本地存储");
        }
        finally{Directory.Delete(folder,true);}
    }
}
