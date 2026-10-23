using System;
using System.Globalization;
using System.Text.Json;

namespace alpha_island.Core
{
    /// <summary>设置项的值类型。决定控件形态、序列化方式与显示格式。</summary>
    public enum SettingKind
    {
        Bool = 0,
        Int = 1,
        Double = 2,
        Enum = 3,
        Text = 4,
        Secret = 5,
        Action = 6
    }

    /// <summary>
    /// 设置页的页键，与 <c>Settings\SettingsWindow.xaml</c> 里导航项的 <c>Tag</c> 一一对应。
    /// 新增设置页时先在这里登记，否则 <c>Nav_Checked</c> 的护栏会直接 return（见该处注释）。
    /// </summary>
    public static class SettingsPageIds
    {
        public const string General = "general";
        public const string Assistant = "assistant";
        public const string Appearance = "appearance";
        public const string Island = "island";
        public const string Media = "media";
        public const string Audio = "audio";
        public const string Lyrics = "lyrics";
        public const string Focus = "focus";
        public const string Notifications = "notifications";
        public const string Behavior = "behavior";
        public const string System = "system";
        public const string Ai = "ai";
        public const string Plugins = "plugins";
        public const string About = "about";
    }

    /// <summary>
    /// 一个设置项的全部元数据：键、类型、范围、默认值、标签、所属页、控件名，以及"读写到哪"。
    /// <para>
    /// 存在的意义（阶段 3.5）：以前加一个布尔开关要改 3 个文件 6 处（IslandOptions 属性 +
    /// CopyFrom + Normalized 夹取 + 加载时读文件 + 应用 + 回写存档）、加一个滑杆要改 7 处；
    /// 现在新设置项一律只做两件事：<see cref="SettingsRegistry"/> 里加一条描述符，
    /// <c>SettingsWindow.xaml</c> 里加一个同名控件。范围夹取、置灰级联、搜索覆盖、
    /// LoadFrom/ApplyChanges 回填全部由注册表统一处理。
    /// </para>
    /// <para>
    /// 键 <c>ControlName</c>/<c>ValueTextName</c> 必须与 XAML 的 <c>x:Name</c> 完全一致（区分大小写）。
    /// 留空表示"还没有 UI"（值仍然会落盘并可被功能代码读取）。
    /// </para>
    /// </summary>
    public sealed class SettingDescriptor
    {
        /// <summary>存储键（同时是 Extras 的字典键，也用于 <c>Find</c>）。</summary>
        public string Key { get; init; } = "";

        public SettingKind Kind { get; init; } = SettingKind.Bool;

        /// <summary>设置页里显示的标题。</summary>
        public string Label { get; init; } = "";

        /// <summary>标题下面的说明文字（同时进搜索索引）。</summary>
        public string Hint { get; init; } = "";

        /// <summary>所属设置页（<see cref="SettingsPageIds"/>）。</summary>
        public string PageId { get; init; } = SettingsPageIds.Behavior;

        /// <summary>控件的 x:Name。Bool → CheckBox/ToggleButton；Int/Double → Slider 或数字 TextBox；Text/Secret → TextBox。</summary>
        public string ControlName { get; init; } = "";

        /// <summary>数值文本的 x:Name（可选，滑杆右侧的 W11Value TextBlock）。</summary>
        public string ValueTextName { get; init; } = "";

        public double Min { get; init; }
        public double Max { get; init; }

        /// <summary>滑杆步进（XAML 里 TickFrequency 用，这里留作说明与校验）。</summary>
        public double Step { get; init; } = 1;

        /// <summary>数值单位，显示在数值后面（例："秒"、"Mbps"、"W"）。</summary>
        public string Unit { get; init; } = "";

        /// <summary>数值格式（<c>ToString</c> 的格式串），默认去掉多余小数。</summary>
        public string Format { get; init; } = "0.##";

        /// <summary>改动后需要重启程序才生效（用于提示，不参与逻辑）。</summary>
        public bool NeedsRestart { get; init; }

        /// <summary>依赖的总开关 Key：它关掉时本项控件与数值文本一起置灰。</summary>
        public string EnabledByKey { get; init; } = "";

        /// <summary>默认值；同时是非法输入的回退值。</summary>
        public object? DefaultValue { get; init; }

        /// <summary>值是否存放在 <see cref="IslandOptions.Extras"/>（新功能）而非强类型属性（已有 30 项）。</summary>
        public bool StoredInExtras { get; init; }

        public Func<IslandOptions, object?> Get { get; init; } = _ => null;
        public Action<IslandOptions, object?> Set { get; init; } = (_, __) => { };

        public bool IsSecret => Kind == SettingKind.Secret;
        public bool IsNumeric => Kind == SettingKind.Int || Kind == SettingKind.Double;

        /// <summary>把任意来源的值（CLR 值 / JsonElement / 字符串）收敛成本项类型并夹取到区间。</summary>
        public object? Coerce(object? value)
        {
            switch (Kind)
            {
                case SettingKind.Bool:
                    return ToBool(value, ToBool(DefaultValue, false));

                case SettingKind.Int:
                {
                    double fallback = ToDouble(DefaultValue, Min);
                    double v = ToDouble(value, fallback);
                    if (double.IsNaN(v) || double.IsInfinity(v)) v = fallback;
                    return (int)Math.Round(Math.Clamp(v, Low, High));
                }

                case SettingKind.Double:
                {
                    double fallback = ToDouble(DefaultValue, Min);
                    double v = ToDouble(value, fallback);
                    if (double.IsNaN(v) || double.IsInfinity(v)) v = fallback;
                    return Math.Clamp(v, Low, High);
                }

                default:
                    return ToText(value) ?? ToText(DefaultValue) ?? "";
            }
        }

        /// <summary>显示用文本（含单位）。</summary>
        public string FormatValue(object? value)
        {
            if (Kind == SettingKind.Bool) return ToBool(value, false) ? "开" : "关";
            if (IsNumeric)
            {
                double v = ToDouble(value, 0);
                string text = v.ToString(string.IsNullOrEmpty(Format) ? "0.##" : Format, CultureInfo.InvariantCulture);
                return string.IsNullOrEmpty(Unit) ? text : text + " " + Unit;
            }
            return ToText(value) ?? "";
        }

        private double Low => Min;
        private double High => Max > Min ? Max : Min;

        // ---- 通用类型转换：必须能处理 JsonElement（Extras 反序列化后的形状） ----

        public static double ToDouble(object? value, double fallback)
        {
            try
            {
                switch (value)
                {
                    case null: return fallback;
                    case double d: return double.IsNaN(d) ? fallback : d;
                    case float f: return f;
                    case int i: return i;
                    case long l: return l;
                    case short s: return s;
                    case byte b: return b;
                    case bool bo: return bo ? 1 : 0;
                    case JsonElement el:
                        if (el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out var n)) return n;
                        if (el.ValueKind == JsonValueKind.True) return 1;
                        if (el.ValueKind == JsonValueKind.False) return 0;
                        if (el.ValueKind == JsonValueKind.String &&
                            double.TryParse(el.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var es)) return es;
                        return fallback;
                    case string str:
                        return double.TryParse(str, NumberStyles.Float, CultureInfo.InvariantCulture, out var ps) ? ps : fallback;
                    default:
                        return double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture),
                            NumberStyles.Float, CultureInfo.InvariantCulture, out var pd) ? pd : fallback;
                }
            }
            catch
            {
                return fallback;
            }
        }

        public static bool ToBool(object? value, bool fallback)
        {
            try
            {
                switch (value)
                {
                    case null: return fallback;
                    case bool b: return b;
                    case JsonElement el:
                        if (el.ValueKind == JsonValueKind.True) return true;
                        if (el.ValueKind == JsonValueKind.False) return false;
                        if (el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out var n)) return Math.Abs(n) > double.Epsilon;
                        if (el.ValueKind == JsonValueKind.String && bool.TryParse(el.GetString(), out var bs)) return bs;
                        return fallback;
                    case string s:
                        if (bool.TryParse(s, out var ps)) return ps;
                        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var pn)) return Math.Abs(pn) > double.Epsilon;
                        return fallback;
                    default:
                        return ToDouble(value, fallback ? 1 : 0) > 0.5;
                }
            }
            catch
            {
                return fallback;
            }
        }

        public static string? ToText(object? value)
        {
            try
            {
                switch (value)
                {
                    case null: return null;
                    case string s: return s;
                    case JsonElement el:
                        return el.ValueKind switch
                        {
                            JsonValueKind.String => el.GetString(),
                            JsonValueKind.Null or JsonValueKind.Undefined => null,
                            _ => el.ToString()
                        };
                    default:
                        return Convert.ToString(value, CultureInfo.InvariantCulture);
                }
            }
            catch
            {
                return null;
            }
        }
    }
}
