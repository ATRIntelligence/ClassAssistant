// =====================================================================
//  课堂助手 Classroom Assistant
//  面向希沃一体机 / Windows 教室大屏的透明常驻助手
//
//   * 屏幕顶部：事件倒计时（默认：距离高考还有 N 天，可在设置中修改）
//   * 屏幕右侧：今日课程表，已上完的课程自动变暗
//   * 下课提醒：到下课时间从右上方滑出提示，停留 3 秒后自动滑走，无声音
//   * 全部为纯透明 UI：除文字本身外不遮挡桌面，且鼠标完全穿透
//
//  编译：双击 build.bat（仅使用 Windows 自带的 .NET Framework 编译器）
//  运行：双击 ClassroomAssistant.exe（首次运行会自动生成 config.json）
// =====================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace ClassroomAssistant
{
    // =================================================================
    //  配置模型（与 config.json 一一对应）
    // =================================================================

    /// <summary>
    /// 课程表中的一行（一个节次）：时间固定，周一到周日各填一门课。
    /// 桌面右侧显示的就是这里的科目名。
    /// </summary>
    public class PeriodItem
    {
        public string Label { get; set; }     // 节次名：早读 / 第一节 / 晚自习一 ...
        public string Start { get; set; }     // 开始时间 HH:mm
        public string End { get; set; }       // 结束时间 HH:mm（该时刻触发下课提醒）
        public string Mon { get; set; }
        public string Tue { get; set; }
        public string Wed { get; set; }
        public string Thu { get; set; }
        public string Fri { get; set; }
        public string Sat { get; set; }
        public string Sun { get; set; }

        public string GetSubject(int weekday)   // 1=周一 ... 7=周日
        {
            switch (weekday)
            {
                case 1: return Mon;
                case 2: return Tue;
                case 3: return Wed;
                case 4: return Thu;
                case 5: return Fri;
                case 6: return Sat;
                default: return Sun;
            }
        }

        public void SetSubject(int weekday, string value)
        {
            switch (weekday)
            {
                case 1: Mon = value; break;
                case 2: Tue = value; break;
                case 3: Wed = value; break;
                case 4: Thu = value; break;
                case 5: Fri = value; break;
                case 6: Sat = value; break;
                default: Sun = value; break;
            }
        }
    }

    // =================================================================
    //  旧版配置（每天一列课程）只用于读取旧 config.json 并自动迁移
    // =================================================================

    public class LegacyCourse
    {
        public string Name { get; set; }
        public string Start { get; set; }
        public string End { get; set; }
    }

    public class LegacyDay
    {
        public int Weekday { get; set; }
        public List<LegacyCourse> Courses { get; set; }
    }

    public class LegacyConfig
    {
        public string EventName { get; set; }          // 旧版：单个倒计时事件
        public string EventDate { get; set; }
        public List<LegacyDay> Schedules { get; set; } // 旧版：每天一列课程
    }

    public static class LegacyImport
    {
        /// 把旧版「每天一列课程」转成「节次 × 星期」课程表。
        public static List<PeriodItem> ToPeriods(List<LegacyDay> schedules)
        {
            // 收集所有出现过的时间段
            List<string[]> slots = new List<string[]>();
            foreach (LegacyDay day in schedules)
            {
                if (day == null || day.Courses == null) continue;
                foreach (LegacyCourse c in day.Courses)
                {
                    if (c == null) continue;
                    string s = c.Start == null ? "" : c.Start.Trim();
                    string e = c.End == null ? "" : c.End.Trim();
                    if (!slots.Any(x => x[0] == s && x[1] == e)) slots.Add(new string[] { s, e });
                }
            }
            slots.Sort(delegate(string[] a, string[] b)
            {
                TimeSpan ta, tb;
                TimeUtil.TryParse(a[0], out ta);
                TimeUtil.TryParse(b[0], out tb);
                return ta.CompareTo(tb);
            });

            List<PeriodItem> periods = new List<PeriodItem>();
            foreach (string[] slot in slots)
            {
                PeriodItem p = new PeriodItem();
                p.Start = slot[0];
                p.End = slot[1];
                p.Label = "";
                foreach (LegacyDay day in schedules)
                {
                    if (day == null || day.Courses == null) continue;
                    foreach (LegacyCourse c in day.Courses)
                    {
                        if (c == null) continue;
                        string s = c.Start == null ? "" : c.Start.Trim();
                        string e = c.End == null ? "" : c.End.Trim();
                        if (s == slot[0] && e == slot[1])
                        {
                            string name = c.Name == null ? "" : c.Name.Trim();
                            p.SetSubject(day.Weekday, name);
                            if (p.Label.Length == 0 && name.Length > 0) p.Label = name;
                        }
                    }
                }
                if (p.Label.Length == 0) p.Label = "第" + (periods.Count + 1) + "节";
                periods.Add(p);
            }
            return periods;
        }
    }

    /// <summary>
    /// 倒计时卡片（类似“倒数日”的一条记录）：名称 + 目标日期 + 可选背景图片。
    /// </summary>
    public class EventItem
    {
        public string Name { get; set; }      // 事件名，例如 高考
        public string Date { get; set; }      // 目标日期 yyyy-MM-dd
        public string Image { get; set; }     // 背景图片（本地文件路径，可空）
        public bool Enabled { get; set; }     // 是否显示在桌面上

        public bool TryGetDate(out DateTime date)
        {
            if (!string.IsNullOrEmpty(Date) &&
                DateTime.TryParseExact(Date.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out date))
                return true;
            return DateTime.TryParse(Date, out date);
        }
    }

    public class AppConfig
    {
        public double FontScale { get; set; }         // 字体缩放，1.0 为默认
        public double DimOpacity { get; set; }        // 已上完课程的亮度（暗色）
        public bool ToastPanel { get; set; }          // 下课提示是否带半透明底色
        public bool? ShowBookmark { get; set; }       // 是否显示右侧“设置”书签
        public bool? TextTopmost { get; set; }        // 文字是否全局置顶（默认否：位于桌面之上、应用之下）
        public string TextStyle { get; set; }         // 文字样式：gold = 暖阳金（默认），white = 经典白
        public List<EventItem> Events { get; set; }   // 倒计时卡片
        public List<PeriodItem> Periods { get; set; } // 一周课程表（节次 × 星期）

        public AppConfig()
        {
            FontScale = 1.0;
            DimOpacity = 0.25;
            ToastPanel = false;
            ShowBookmark = true;
            TextTopmost = false;
            TextStyle = "gold";
            Events = null;      // 未指定时由 Normalize 填入默认卡片
            Periods = null;     // 未指定时由 Normalize 填入默认课程表
        }

        public void Normalize()
        {
            if (FontScale <= 0 || FontScale > 4) FontScale = 1.0;
            if (DimOpacity <= 0 || DimOpacity > 1) DimOpacity = 0.25;
            if (ShowBookmark == null) ShowBookmark = true;   // 老配置里没有该字段时默认显示
            if (TextTopmost == null) TextTopmost = false;    // 默认：桌面层显示
            if (string.IsNullOrEmpty(TextStyle)) TextStyle = "gold";   // 默认暖阳金
            TextStyle = string.Equals(TextStyle, "white", StringComparison.OrdinalIgnoreCase) ? "white" : "gold";
            if (Events == null) Events = DefaultEvents();
            foreach (EventItem e in Events)
            {
                if (e == null) continue;
                if (e.Name == null) e.Name = "";
                if (e.Date == null) e.Date = "";
                if (e.Image == null) e.Image = "";
            }
            Events.RemoveAll(x => x == null);
            if (Periods == null) Periods = DefaultPeriods();   // 明确写成空数组表示一张空课表，不补默认
            foreach (PeriodItem p in Periods)
            {
                if (p == null) continue;
                if (p.Label == null) p.Label = "";
                if (p.Start == null) p.Start = "";
                if (p.End == null) p.End = "";
                for (int d = 1; d <= 7; d++)
                    if (p.GetSubject(d) == null) p.SetSubject(d, "");
            }
            Periods.RemoveAll(x => x == null);
            Periods.Sort(delegate(PeriodItem a, PeriodItem b)
            {
                TimeSpan ta, tb;
                TimeUtil.TryParse(a.Start, out ta);
                TimeUtil.TryParse(b.Start, out tb);
                return ta.CompareTo(tb);
            });
        }

        public static AppConfig Clone(AppConfig cfg)
        {
            JavaScriptSerializer js = new JavaScriptSerializer();
            js.MaxJsonLength = int.MaxValue;
            return js.Deserialize<AppConfig>(js.Serialize(cfg));
        }

        /// 下一次 6 月 7 日（高考日期），今天已过则取明年。
        private static string DefaultEventDate()
        {
            DateTime today = DateTime.Today;
            DateTime day = new DateTime(today.Year, 6, 7);
            if (today > day) day = day.AddYears(1);
            return day.ToString("yyyy-MM-dd");
        }

        /// 默认倒计时卡片：一张“高考”。
        private static List<EventItem> DefaultEvents()
        {
            List<EventItem> list = new List<EventItem>();
            EventItem e = new EventItem();
            e.Name = "高考";
            e.Date = DefaultEventDate();
            e.Image = "";
            e.Enabled = true;
            list.Add(e);
            return list;
        }

        /// 默认课程表：上午「早读 + 四节课」，下午三节课，晚上两节晚自习。
        private static List<PeriodItem> DefaultPeriods()
        {
            string[,] sample = new string[,]
            {
                { "早读",     "07:20", "08:00", "早读" },
                { "第一节",   "08:00", "08:45", "语文" },
                { "第二节",   "08:55", "09:40", "数学" },
                { "第三节",   "10:00", "10:45", "英语" },
                { "第四节",   "10:55", "11:40", "物理" },
                { "第五节",   "14:00", "14:45", "化学" },
                { "第六节",   "14:55", "15:40", "生物" },
                { "第七节",   "16:00", "16:45", "政治" },
                { "晚自习一", "19:00", "19:45", "自习" },
                { "晚自习二", "19:55", "20:40", "自习" }
            };

            List<PeriodItem> list = new List<PeriodItem>();
            for (int i = 0; i < sample.GetLength(0); i++)
            {
                PeriodItem p = new PeriodItem();
                p.Label = sample[i, 0];
                p.Start = sample[i, 1];
                p.End = sample[i, 2];
                for (int d = 1; d <= 7; d++)
                    p.SetSubject(d, d <= 5 ? sample[i, 3] : "");   // 周六、周日默认没课
                list.Add(p);
            }
            return list;
        }
    }

    public static class ConfigStore
    {
        private static string _path;

        /// 最近一次读取 config.json 失败的原因（成功读取时为 null）。
        public static string LastError;

        /// 是否刚把旧版配置迁移成了新格式。
        public static bool WasMigrated;

        /// 最近一次保存的提示信息（如“程序目录不可写，已存到用户目录”）。
        public static string SaveNotice;

        public static string FilePath
        {
            get
            {
                if (_path == null)
                    _path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");
                return _path;
            }
        }

        /// 备用位置：程序目录不可写时（如装在 Program Files）改存当前用户目录。
        public static string FallbackPath
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "ClassroomAssistant", "config.json");
            }
        }

        /// 实际读取哪份配置：程序目录与用户目录同时存在时取较新的一份。
        private static string ActivePath()
        {
            bool a = File.Exists(FilePath);
            bool b = File.Exists(FallbackPath);
            if (a && b)
                return File.GetLastWriteTimeUtc(FilePath) >= File.GetLastWriteTimeUtc(FallbackPath)
                    ? FilePath : FallbackPath;
            return a ? FilePath : FilePath;
        }

        public static AppConfig Load()
        {
            LastError = null;
            WasMigrated = false;
            SaveNotice = null;
            AppConfig cfg = null;
            string active = ActivePath();
            if (File.Exists(active))
            {
                try
                {
                    JavaScriptSerializer js = new JavaScriptSerializer();
                    js.MaxJsonLength = int.MaxValue;
                    string json = File.ReadAllText(active, Encoding.UTF8);
                    cfg = js.Deserialize<AppConfig>(json);

                    // 兼容旧版配置：单事件 → 倒计时卡片；每天一列课程 → 整周课程表
                    if (cfg != null && (cfg.Events == null || cfg.Periods == null || cfg.Periods.Count == 0))
                    {
                        LegacyConfig legacy = js.Deserialize<LegacyConfig>(json);
                        if (legacy != null && cfg.Events == null && !string.IsNullOrEmpty(legacy.EventName))
                        {
                            EventItem ev = new EventItem();
                            ev.Name = legacy.EventName;
                            ev.Date = legacy.EventDate;
                            ev.Image = "";
                            ev.Enabled = true;
                            cfg.Events = new List<EventItem>();
                            cfg.Events.Add(ev);
                            WasMigrated = true;
                        }
                        if (legacy != null && (cfg.Periods == null || cfg.Periods.Count == 0)
                            && legacy.Schedules != null && legacy.Schedules.Count > 0)
                        {
                            List<PeriodItem> periods = LegacyImport.ToPeriods(legacy.Schedules);
                            if (periods.Count > 0)
                            {
                                cfg.Periods = periods;
                                WasMigrated = true;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    LastError = ex.Message;
                    cfg = null;
                }
            }
            if (cfg == null) cfg = new AppConfig();
            cfg.Normalize();

            // 迁移过就把新格式写回磁盘，避免每次启动都提示
            if (WasMigrated)
            {
                try { Save(cfg); }
                catch { }
            }
            return cfg;
        }

        public static void Save(AppConfig cfg)
        {
            cfg.Normalize();
            SaveNotice = null;
            JavaScriptSerializer js = new JavaScriptSerializer();
            js.MaxJsonLength = int.MaxValue;
            string json = js.Serialize(cfg);
            try
            {
                File.WriteAllText(FilePath, json, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                // 程序目录不可写（如装在 Program Files、只读介质）：改存当前用户目录
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(FallbackPath));
                    File.WriteAllText(FallbackPath, json, Encoding.UTF8);
                    SaveNotice = "程序目录不可写（" + ex.Message + "），设置已改存到：\r\n" + FallbackPath;
                }
                catch (Exception ex2)
                {
                    throw new IOException("设置无法保存：" + ex2.Message, ex2);
                }
            }
        }
    }

    // =================================================================
    //  工具
    // =================================================================

    public static class TimeUtil
    {
        /// 支持 "08:00"、"8:00"，容忍中文冒号。
        public static bool TryParse(string text, out TimeSpan time)
        {
            time = TimeSpan.Zero;
            if (string.IsNullOrEmpty(text)) return false;
            string s = text.Trim().Replace('：', ':');
            if (s.Length == 0) return false;
            return TimeSpan.TryParse(s, CultureInfo.InvariantCulture, out time);
        }

        public static string DayName(DateTime date)
        {
            switch ((int)date.DayOfWeek)
            {
                case 1: return "周一";
                case 2: return "周二";
                case 3: return "周三";
                case 4: return "周四";
                case 5: return "周五";
                case 6: return "周六";
                default: return "周日";
            }
        }

        public static int Weekday(DateTime date)
        {
            int d = (int)date.DayOfWeek;
            return d == 0 ? 7 : d;
        }
    }

    public class Resolved
    {
        public PeriodItem Item;
        public string Subject;          // 当天上的科目（桌面显示的文字）
        public DateTime Start;
        public DateTime End;
    }

    public static class ScheduleUtil
    {
        /// 今天要上的课（节次 + 当天科目），按开始时间排序；当天没填科目的节次不显示。
        public static List<Resolved> ForDay(AppConfig cfg, DateTime date)
        {
            List<Resolved> list = new List<Resolved>();
            if (cfg == null || cfg.Periods == null) return list;
            date = date.Date;                 // 只取日期：否则 date + 时间 会把当前时刻也算进去
            int weekday = TimeUtil.Weekday(date);

            foreach (PeriodItem p in cfg.Periods)
            {
                if (p == null) continue;
                string subject = p.GetSubject(weekday);
                if (subject == null || subject.Trim().Length == 0) continue;   // 当天没有这节课

                Resolved r = new Resolved();
                r.Item = p;
                r.Subject = subject.Trim();

                TimeSpan t;
                bool hasStart = TimeUtil.TryParse(p.Start, out t);
                DateTime start = hasStart ? date + t : DateTime.MinValue;
                bool hasEnd = TimeUtil.TryParse(p.End, out t);
                DateTime end = hasEnd ? date + t : DateTime.MinValue;

                if (!hasStart && !hasEnd) continue;          // 没有时间的行忽略
                if (!hasStart) start = end.AddHours(-1);
                if (!hasEnd) end = start.AddHours(1);
                if (end < start) end = end.AddDays(1);       // 跨夜（如晚自习）的情况

                r.Start = start;
                r.End = end;
                list.Add(r);
            }
            list.Sort(delegate(Resolved a, Resolved b) { return a.Start.CompareTo(b.Start); });
            return list;
        }

        public static CourseState GetState(Resolved r, DateTime now)
        {
            if (now >= r.End) return CourseState.Finished;
            if (now >= r.Start) return CourseState.Current;
            return CourseState.Upcoming;
        }
    }

    public enum CourseState
    {
        Upcoming,
        Current,
        Finished
    }

    public static class WpfUtil
    {
        /// <summary>
        /// 桌面文字样式（保留两个版本）：
        /// · "gold"  暖阳金：金橙渐变，更醒目、不单调（默认）
        /// · "white" 经典白：原来的纯白文字
        /// </summary>
        private static string _style = "gold";

        public static void SetTextStyle(string style)
        {
            _style = string.Equals(style, "white", StringComparison.OrdinalIgnoreCase) ? "white" : "gold";
        }

        public static string TextStyleName
        {
            get { return _style; }
        }

        /// 主文字颜色：暖阳金 = 三段渐变；经典白 = 纯白。
        public static Brush MainBrush()
        {
            if (_style == "white") return Brushes.White;
            LinearGradientBrush g = new LinearGradientBrush();
            g.StartPoint = new Point(0, 0);
            g.EndPoint = new Point(1, 1);
            g.GradientStops.Add(new GradientStop(Color.FromRgb(0xFF, 0xE2, 0x8A), 0.0));
            g.GradientStops.Add(new GradientStop(Color.FromRgb(0xFF, 0xB4, 0x4F), 0.55));
            g.GradientStops.Add(new GradientStop(Color.FromRgb(0xFF, 0x83, 0x4F), 1.0));
            return g;
        }

        /// 次要文字颜色：比主色柔和一档，层次更分明。
        public static Brush SoftBrush()
        {
            if (_style == "white") return Brushes.White;
            return new SolidColorBrush(Color.FromRgb(0xFF, 0xE7, 0xC2));
        }

        public static DropShadowEffect TextShadow(double radius, double opacity)
        {
            DropShadowEffect e = new DropShadowEffect();
            e.Color = Colors.Black;
            e.BlurRadius = radius;
            e.ShadowDepth = 0;
            e.Direction = 270;
            e.Opacity = opacity;
            return e;
        }

        /// 桌面主文字（带阴影，保证在任何桌面上都看得清）。
        public static TextBlock OverlayText(string text, double size, FontWeight weight, double opacity)
        {
            TextBlock t = new TextBlock();
            t.Text = text;
            t.FontSize = size;
            t.FontWeight = weight;
            t.Foreground = MainBrush();
            t.Opacity = opacity;
            t.Effect = TextShadow(10, _style == "white" ? 0.85 : 0.95);
            return t;
        }

        /// 桌面次要文字（目标日期、状态标签等）。
        public static TextBlock OverlaySoftText(string text, double size, FontWeight weight, double opacity)
        {
            TextBlock t = OverlayText(text, size, weight, opacity);
            t.Foreground = SoftBrush();
            return t;
        }
    }

    /// <summary>
    /// 提示框工具：保证对话框永远显示在最上层。
    /// 若对话框被其它窗口（如置顶的设置窗口、浏览器）挡住，程序会像“卡住”一样
    /// 无法修改和保存设置 —— 所有弹窗都必须走这里。
    /// </summary>
    public static class Notice
    {
        /// 有宿主窗口（如设置窗口）：对话框跟随宿主并永远在其之上。
        public static void Show(Window owner, string message, MessageBoxImage image)
        {
            if (owner != null)
            {
                MessageBox.Show(owner, message, "课堂助手", MessageBoxButton.OK, image);
                return;
            }
            Show(message, image);
        }

        /// 无宿主窗口（如启动提示）：用一个隐藏的置顶宿主弹出，确保不被任何窗口遮挡。
        public static void Show(string message, MessageBoxImage image)
        {
            Window host = new Window();
            host.WindowStyle = WindowStyle.None;
            host.AllowsTransparency = true;
            host.Background = Brushes.Transparent;
            host.Width = 1;
            host.Height = 1;
            host.Left = -10000;
            host.Top = -10000;
            host.Topmost = true;
            host.ShowInTaskbar = false;
            host.Show();
            try
            {
                MessageBox.Show(host, message, "课堂助手", MessageBoxButton.OK, image);
            }
            finally
            {
                host.Close();
            }
        }
    }

    // =================================================================
    //  窗口穿透 / 置顶
    // =================================================================

    internal static class NativeMethods
    {
        public const int GWL_EXSTYLE = -20;
        public const int WS_EX_TRANSPARENT = 0x00000020;
        public const int WS_EX_TOOLWINDOW = 0x00000080;
        public const int WS_EX_LAYERED = 0x00080000;
        public const int WS_EX_NOACTIVATE = 0x08000000;

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
        private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
        private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
        private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
        private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

        /// 完全穿透鼠标点击、不出现在任务栏和 Alt+Tab、不抢焦点。
        public static void MakeClickThrough(IntPtr hwnd)
        {
            long ex = IntPtr.Size == 8
                ? GetWindowLongPtr64(hwnd, GWL_EXSTYLE).ToInt64()
                : (long)GetWindowLong32(hwnd, GWL_EXSTYLE);
            ex |= WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_LAYERED;
            if (IntPtr.Size == 8) SetWindowLongPtr64(hwnd, GWL_EXSTYLE, new IntPtr(ex));
            else SetWindowLong32(hwnd, GWL_EXSTYLE, (int)ex);
        }

        /// 可以点击、但不抢焦点、不出现在任务栏和 Alt+Tab（书签标签用）。
        public static void MakeNonActivating(IntPtr hwnd)
        {
            long ex = IntPtr.Size == 8
                ? GetWindowLongPtr64(hwnd, GWL_EXSTYLE).ToInt64()
                : (long)GetWindowLong32(hwnd, GWL_EXSTYLE);
            ex |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_LAYERED;
            if (IntPtr.Size == 8) SetWindowLongPtr64(hwnd, GWL_EXSTYLE, new IntPtr(ex));
            else SetWindowLong32(hwnd, GWL_EXSTYLE, (int)ex);
        }

        // ---- 桌面层（把窗口挂到桌面窗口上）----

        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr FindWindowW(string className, string windowName);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr FindWindowExW(IntPtr parent, IntPtr afterChild, string className, string windowName);

        [DllImport("user32.dll")]
        public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern IntPtr SendMessageTimeoutW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam,
            uint flags, uint timeout, out IntPtr result);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

        public const uint SWP_NOSIZE = 0x0001;
        public const uint SWP_NOMOVE = 0x0002;
        public const uint SWP_NOACTIVATE = 0x0010;
        public const uint SWP_NOSENDCHANGING = 0x0400;

        [DllImport("user32.dll")]
        public static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassNameW(IntPtr hWnd, StringBuilder sb, int maxCount);

        public static string ClassNameOf(IntPtr hWnd)
        {
            StringBuilder sb = new StringBuilder(256);
            GetClassNameW(hWnd, sb, 256);
            return sb.ToString();
        }
    }

    /// <summary>
    /// 桌面层：把窗口挂到桌面窗口（Progman / WorkerW）上，
    /// 效果等同“桌面挂件”——位于壁纸之上、所有普通窗口之下。
    /// </summary>
    public static class DesktopLayer
    {
        private static IntPtr _desktop = IntPtr.Zero;

        /// 最近一次找到的桌面窗口（诊断用）。
        public static IntPtr CurrentDesktop { get { return _desktop; } }

        private static IntPtr FindDesktopWindow()
        {
            // 用枚举找 Progman（FindWindow 在部分环境下不可靠）
            IntPtr progman = IntPtr.Zero;
            IntPtr workerw = IntPtr.Zero;
            NativeMethods.EnumWindows(delegate(IntPtr top, IntPtr lParam)
            {
                if (progman == IntPtr.Zero && NativeMethods.ClassNameOf(top) == "Progman") progman = top;
                // 桌面图标窗口（SHELLDLL_DefView）所在的顶层窗口，其后的 WorkerW 就是壁纸层
                if (NativeMethods.FindWindowExW(top, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero)
                {
                    IntPtr w = NativeMethods.FindWindowExW(IntPtr.Zero, top, "WorkerW", null);
                    if (w != IntPtr.Zero) workerw = w;
                }
                return true;
            }, IntPtr.Zero);

            // 让 Progman 生成壁纸层窗口（Windows 10/11 通用做法）
            if (progman != IntPtr.Zero)
            {
                IntPtr result;
                NativeMethods.SendMessageTimeoutW(progman, 0x052C, IntPtr.Zero, IntPtr.Zero, 2, 1000, out result);
            }

            return workerw != IntPtr.Zero ? workerw : progman;
        }

        /// <summary>
        /// 把窗口放到桌面之上、所有普通应用窗口之下（即“桌面层”）。
        /// 不建立父子关系、不抢焦点：应用窗口不全屏时，桌面露出的部分照样能看到文字，
        /// 且完全不需要点击或等待。
        /// </summary>
        public static bool PlaceAboveDesktop(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return false;
            IntPtr desktop = FindDesktopWindow();
            if (desktop == IntPtr.Zero) return false;
            _desktop = desktop;

            // Z 序自上而下枚举，找到紧挨“桌面窗口”上方的那个窗口
            IntPtr above = IntPtr.Zero;
            NativeMethods.EnumWindows(delegate(IntPtr top, IntPtr lParam)
            {
                if (top == hwnd) return true;          // 跳过自己
                if (top == desktop) return false;      // 已经到桌面，停止
                above = top;
                return true;
            }, IntPtr.Zero);

            if (above == IntPtr.Zero) return false;
            NativeMethods.SetWindowPos(hwnd, above, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE
                | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOSENDCHANGING);
            return true;
        }
    }

    // =================================================================
    //  常驻覆盖层：顶部倒计时 + 右侧今日课程
    // =================================================================

    public class OverlayWindow : Window
    {
        /// 窗口被系统销毁（例如随桌面层一起重建）时通知外壳。
        public event Action Died;

        /// 文字显示模式诊断（自检日志用）。
        public string DesktopLayerStatus = "not attempted";

        private bool _wantVisible = true;
        private DispatcherTimer _visibilityTimer;
        private AppConfig _cfg;
        private readonly WrapPanel _cards;
        private readonly TextBlock _dayTitle;
        private readonly StackPanel _rightPanel;
        private readonly StackPanel _list;
        private const double RightEdgeMargin = 18;   // 课程表贴紧屏幕右边框
        private DispatcherTimer _timer;

        public int BuiltRows { get; private set; }

        public OverlayWindow(AppConfig cfg)
        {
            _cfg = cfg;

            Title = "课堂助手覆盖层";
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            ShowActivated = false;
            ResizeMode = ResizeMode.NoResize;
            Focusable = false;
            FontFamily = new FontFamily("Microsoft YaHei");
            UseLayoutRounding = true;

            Grid root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            // ---- 顶部：倒计时卡片（支持多事件、可导入图片作背景）----
            _cards = new WrapPanel();
            _cards.Orientation = Orientation.Horizontal;
            _cards.HorizontalAlignment = HorizontalAlignment.Center;
            _cards.Margin = new Thickness(24, 14, 24, 0);
            Grid.SetRow(_cards, 0);
            root.Children.Add(_cards);

            // ---- 右侧：今日课程 ----
            StackPanel right = new StackPanel();
            right.Orientation = Orientation.Vertical;
            right.HorizontalAlignment = HorizontalAlignment.Right;
            right.VerticalAlignment = VerticalAlignment.Center;
            _rightPanel = right;
            ApplyRightMargin();

            _dayTitle = WpfUtil.OverlaySoftText("今日课程", 20 * _cfg.FontScale, FontWeights.Normal, 0.55);
            _dayTitle.HorizontalAlignment = HorizontalAlignment.Right;
            _dayTitle.Margin = new Thickness(0, 0, 2, 16);
            right.Children.Add(_dayTitle);

            _list = new StackPanel();
            _list.Orientation = Orientation.Vertical;
            right.Children.Add(_list);

            Grid.SetRow(right, 1);
            root.Children.Add(right);

            Content = root;

            SourceInitialized += OnSourceInitialized;
            Loaded += delegate { FitToScreen(); Refresh(); };
            Closed += delegate
            {
                if (_timer != null) _timer.Stop();
                if (_visibilityTimer != null) _visibilityTimer.Stop();
                Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            };
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        }

        public void Start()
        {
            FitToScreen();
            Refresh();
            if (_timer == null)
            {
                _timer = new DispatcherTimer();
                _timer.Interval = TimeSpan.FromSeconds(15);
                _timer.Tick += delegate { Refresh(); EnsureDesktopLayer(); };
            }
            _timer.Start();

            // 桌面层模式：定期校正 Z 序（被顶上去就压回来），无需点击、无延时
            if (_visibilityTimer == null)
            {
                _visibilityTimer = new DispatcherTimer();
                _visibilityTimer.Interval = TimeSpan.FromSeconds(2);
                _visibilityTimer.Tick += delegate { EnsureDesktopLayer(); };
            }
            _visibilityTimer.Start();
        }

        public void UpdateConfig(AppConfig cfg)
        {
            _cfg = cfg;
            ApplyRightMargin();
            Refresh();
        }

        /// 课程表贴紧屏幕右边框（暂不避让右侧书签）。
        private void ApplyRightMargin()
        {
            if (_rightPanel != null)
                _rightPanel.Margin = new Thickness(0, 0, RightEdgeMargin * _cfg.FontScale, 0);
        }

        private void OnDisplaySettingsChanged(object sender, EventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(FitToScreen));
        }

        private void FitToScreen()
        {
            Left = SystemParameters.VirtualScreenLeft;
            Top = SystemParameters.VirtualScreenTop;
            Width = SystemParameters.VirtualScreenWidth;
            Height = SystemParameters.VirtualScreenHeight;
        }

        private void OnSourceInitialized(object sender, EventArgs e)
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            NativeMethods.MakeClickThrough(hwnd);
            ApplyTextMode();
        }

        /// <summary>
        /// 文字显示模式：
        /// · 默认“桌面层”：位于桌面之上、普通应用之下 —— 应用不全屏时，
        ///   桌面露出的部分照样能看到文字；切换桌面立即可见，无需点击、没有延时。
        /// · 设置里可改为“全局置顶”：盖住所有窗口。
        /// </summary>
        public void ApplyTextMode()
        {
            bool topmost = _cfg.TextTopmost == true;
            Topmost = topmost;
            if (!topmost) DesktopLayer.PlaceAboveDesktop(new WindowInteropHelper(this).Handle);
            DesktopLayerStatus = "textTopmost=" + topmost
                + " desktop=" + DesktopLayer.CurrentDesktop
                + " topmost=" + Topmost;
        }

        /// 定期校正：保持“桌面层”位置（被顶上去就压回来）；窗口被销毁则通知外壳重建。
        private void EnsureDesktopLayer()
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (!NativeMethods.IsWindow(hwnd))
            {
                if (Died != null) Died();
                return;
            }
            if (_wantVisible && _cfg.TextTopmost != true) DesktopLayer.PlaceAboveDesktop(hwnd);
        }

        /// 外壳开关（托盘菜单“隐藏/显示覆盖层”）。
        public void SetDesktopVisible(bool visible)
        {
            _wantVisible = visible;
            if (visible) Show();
            else Hide();
        }

        // ---- 内容刷新 ----

        public void Refresh()
        {
            DateTime now = DateTime.Now;
            WpfUtil.SetTextStyle(_cfg.TextStyle);      // 应用当前文字样式（暖阳金 / 经典白）

            // 顶部：横版倒计时文字（带背景图片的事件显示为横版图片卡片）
            _cards.Children.Clear();
            foreach (EventItem ev in _cfg.Events)
            {
                if (ev == null || !ev.Enabled) continue;
                UIElement block = BuildEventBlock(ev, now.Date);
                if (block != null) _cards.Children.Add(block);
            }

            // 今日课程：只显示科目（不显示时间），整体贴紧屏幕右边框
            _dayTitle.Text = string.Format("今日课程 · {0}", TimeUtil.DayName(now));
            _list.Children.Clear();
            List<Resolved> courses = ScheduleUtil.ForDay(_cfg, now);
            BuiltRows = courses.Count;

            if (courses.Count == 0)
            {
                TextBlock empty = WpfUtil.OverlaySoftText("今天没有课程安排", 22 * _cfg.FontScale, FontWeights.Normal, 0.35);
                empty.HorizontalAlignment = HorizontalAlignment.Right;
                _list.Children.Add(empty);
                return;
            }

            foreach (Resolved r in courses)
            {
                CourseState state = ScheduleUtil.GetState(r, now);

                StackPanel row = new StackPanel();
                row.Orientation = Orientation.Horizontal;
                row.HorizontalAlignment = HorizontalAlignment.Right;
                row.Margin = new Thickness(0, 7, 0, 7);

                TextBlock nameText = WpfUtil.OverlayText(r.Subject, 26 * _cfg.FontScale, FontWeights.SemiBold, 1.0);
                nameText.VerticalAlignment = VerticalAlignment.Center;
                row.Children.Add(nameText);

                if (state == CourseState.Current)
                {
                    TextBlock tag = WpfUtil.OverlaySoftText("进行中", 14 * _cfg.FontScale, FontWeights.Normal, 0.55);
                    tag.VerticalAlignment = VerticalAlignment.Bottom;
                    tag.Margin = new Thickness(12, 0, 0, 4);
                    row.Children.Add(tag);
                }

                // 上完的课程整行变暗
                if (state == CourseState.Finished) row.Opacity = _cfg.DimOpacity;

                _list.Children.Add(row);
            }
        }

        /// <summary>
        /// 生成一条横版倒计时（原顶头样式）：距离XX还有 N 天 + 目标日期；
        /// 事件设置了背景图片时，文字显示在横版图片卡片上。
        /// </summary>
        private UIElement BuildEventBlock(EventItem ev, DateTime today)
        {
            double s = _cfg.FontScale;
            DateTime target;
            if (!ev.TryGetDate(out target)) return null;
            int days = (target.Date - today).Days;

            string name = (ev.Name != null && ev.Name.Trim().Length > 0) ? ev.Name.Trim() : "目标";
            string line1;
            if (days > 0) line1 = string.Format("距离{0}还有 {1} 天", name, days);
            else if (days == 0) line1 = string.Format("今天就是{0}，加油！", name);
            else line1 = string.Format("{0}已经过去 {1} 天", name, -days);
            string line2 = string.Format("目标日期：{0}年{1}月{2}日", target.Year, target.Month, target.Day);

            StackPanel text = new StackPanel();
            text.Orientation = Orientation.Vertical;
            text.HorizontalAlignment = HorizontalAlignment.Center;
            text.VerticalAlignment = VerticalAlignment.Center;

            TextBlock t1 = WpfUtil.OverlayText(line1, 42 * s, FontWeights.Bold, 1.0);
            t1.HorizontalAlignment = HorizontalAlignment.Center;
            text.Children.Add(t1);

            TextBlock t2 = WpfUtil.OverlaySoftText(line2, 18 * s, FontWeights.Normal, 0.5);
            t2.HorizontalAlignment = HorizontalAlignment.Center;
            t2.Margin = new Thickness(0, 6, 0, 0);
            text.Children.Add(t2);

            ImageBrush brush = LoadImageBrush(ev.Image);
            if (brush == null)
            {
                // 没有背景图片：纯横版文字，完全不遮挡桌面
                return text;
            }

            // 有背景图片：横版图片卡片（图片铺满 + 压暗一层保证白字清晰）
            Border card = new Border();
            card.Width = 580 * s;
            card.Height = 132 * s;
            card.CornerRadius = new CornerRadius(14);
            card.Margin = new Thickness(10);
            card.Background = brush;
            card.Effect = WpfUtil.TextShadow(18, 0.6);
            Grid g = new Grid();
            g.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(105, 0, 0, 0)),
                CornerRadius = new CornerRadius(14)
            });
            g.Children.Add(text);
            card.Child = g;
            return card;
        }

        /// 读取本地图片作为卡片背景；失败（路径为空 / 文件不存在 / 图片损坏）返回 null。
        private static ImageBrush LoadImageBrush(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path)) return null;
                string full = path.Trim();
                if (!File.Exists(full)) return null;
                BitmapImage bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(full, UriKind.Absolute);
                bmp.DecodePixelWidth = 480;          // 卡片用图，缩小解码省内存
                bmp.EndInit();
                bmp.Freeze();
                ImageBrush brush = new ImageBrush(bmp);
                brush.Stretch = Stretch.UniformToFill;
                return brush;
            }
            catch
            {
                return null;
            }
        }

        /// 自检/排障用：把覆盖层渲染成 PNG 快照（可直接看到高亮 / 变暗效果）。
        public void SaveSnapshot(string path)
        {
            double w = (ActualWidth > 0) ? ActualWidth : SystemParameters.VirtualScreenWidth;
            double h = (ActualHeight > 0) ? ActualHeight : SystemParameters.VirtualScreenHeight;
            if (double.IsNaN(w) || w <= 0) w = 1280;
            if (double.IsNaN(h) || h <= 0) h = 720;
            Measure(new Size(w, h));
            Arrange(new Rect(0, 0, w, h));
            UpdateLayout();
            RenderTargetBitmap rtb = new RenderTargetBitmap((int)w, (int)h, 96, 96, PixelFormats.Pbgra32);
            Visual target = Content as Visual;      // 未显示过的 Window 直接渲染会得到空白
            if (target == null) target = this;
            rtb.Render(target);
            PngBitmapEncoder png = new PngBitmapEncoder();
            png.Frames.Add(BitmapFrame.Create(rtb));
            using (FileStream fs = File.Create(path))
            {
                png.Save(fs);
            }
        }
    }

    // =================================================================
    //  下课提醒：从右上方滑入，停留 3 秒后滑出，无提示音
    // =================================================================

    public class ToastWindow : Window
    {
        private AppConfig _cfg;
        private readonly TextBlock _title;
        private readonly TextBlock _line1;
        private readonly TextBlock _line2;
        private readonly Border _panel;
        private readonly DispatcherTimer _hideTimer;

        public ToastWindow(AppConfig cfg)
        {
            _cfg = cfg;

            Title = "课堂助手下课提醒";
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            ShowActivated = false;
            ResizeMode = ResizeMode.NoResize;
            Focusable = false;
            FontFamily = new FontFamily("Microsoft YaHei");
            SizeToContent = SizeToContent.Height;
            Width = 470 * _cfg.FontScale;

            _panel = new Border();
            _panel.CornerRadius = new CornerRadius(12);
            _panel.Padding = new Thickness(24, 18, 24, 18);
            _panel.HorizontalAlignment = HorizontalAlignment.Stretch;
            ApplyPanelStyle();

            StackPanel text = new StackPanel();
            text.Orientation = Orientation.Vertical;

            _title = WpfUtil.OverlayText("下课提醒", 24 * _cfg.FontScale, FontWeights.Bold, 1.0);
            text.Children.Add(_title);

            _line1 = WpfUtil.OverlaySoftText("", 18 * _cfg.FontScale, FontWeights.Normal, 0.92);
            _line1.Margin = new Thickness(0, 8, 0, 0);
            _line1.TextWrapping = TextWrapping.Wrap;
            text.Children.Add(_line1);

            _line2 = WpfUtil.OverlaySoftText("", 17 * _cfg.FontScale, FontWeights.Normal, 0.65);
            _line2.Margin = new Thickness(0, 4, 0, 0);
            _line2.TextWrapping = TextWrapping.Wrap;
            text.Children.Add(_line2);

            _panel.Child = text;
            Content = _panel;

            _hideTimer = new DispatcherTimer();
            _hideTimer.Interval = TimeSpan.FromSeconds(3);
            _hideTimer.Tick += delegate
            {
                _hideTimer.Stop();
                SlideOut();
            };

            SourceInitialized += OnSourceInitialized;
        }

        public void UpdateConfig(AppConfig cfg)
        {
            _cfg = cfg;
            ApplyPanelStyle();
        }

        private void ApplyPanelStyle()
        {
            if (_cfg != null && _cfg.ToastPanel)
            {
                // 可选：半透明底色，更容易看清（默认关闭，保持纯文字）
                _panel.Background = new SolidColorBrush(Color.FromArgb(120, 18, 20, 26));
            }
            else
            {
                _panel.Background = Brushes.Transparent;
            }
        }

        private void OnSourceInitialized(object sender, EventArgs e)
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            NativeMethods.MakeClickThrough(hwnd);
        }

        public void ShowToast(string title, string line1, string line2)
        {
            WpfUtil.SetTextStyle(_cfg != null ? _cfg.TextStyle : null);
            _title.Foreground = WpfUtil.MainBrush();
            _line1.Foreground = WpfUtil.SoftBrush();
            _line2.Foreground = WpfUtil.SoftBrush();
            _title.Text = title;
            _line1.Text = line1;
            _line2.Text = line2;

            double right = SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth;
            double targetLeft = right - Width - 46;
            Top = SystemParameters.VirtualScreenTop + 26;

            // 取消可能正在进行的动画，从右侧屏外开始
            BeginAnimation(Window.LeftProperty, null);
            Left = targetLeft + Width + 80;
            if (!IsVisible) Show();

            DoubleAnimation slideIn = new DoubleAnimation(targetLeft, TimeSpan.FromMilliseconds(320));
            slideIn.EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut };
            BeginAnimation(Window.LeftProperty, slideIn);

            _hideTimer.Stop();
            _hideTimer.Start();
        }

        private void SlideOut()
        {
            double offscreen = Left + Width + 80;
            DoubleAnimation slideOut = new DoubleAnimation(offscreen, TimeSpan.FromMilliseconds(280));
            slideOut.EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn };
            slideOut.Completed += delegate
            {
                BeginAnimation(Window.LeftProperty, null);
                Hide();
            };
            BeginAnimation(Window.LeftProperty, slideOut);
        }
    }

    // =================================================================
    //  下课提醒调度：课程到点时触发一次，不发声，不补发历史提醒
    // =================================================================

    public class ReminderService
    {
        public event Action<string, string, string> Fire;

        private AppConfig _cfg;
        private readonly DispatcherTimer _timer;
        private readonly HashSet<string> _fired = new HashSet<string>();
        private readonly DateTime _startedAt = DateTime.Now;
        private DateTime _day = DateTime.Today;

        public ReminderService(AppConfig cfg)
        {
            _cfg = cfg;
            _timer = new DispatcherTimer();
            _timer.Interval = TimeSpan.FromSeconds(15);
            _timer.Tick += delegate { Tick(); };
        }

        public void Start()
        {
            _timer.Start();
            Tick();
        }

        public void Stop()
        {
            _timer.Stop();
        }

        public void UpdateConfig(AppConfig cfg)
        {
            _cfg = cfg;
        }

        private void Tick()
        {
            DateTime now = DateTime.Now;
            if (now.Date != _day)
            {
                _day = now.Date;
                _fired.Clear();
            }

            List<Resolved> list = ScheduleUtil.ForDay(_cfg, now);
            for (int i = 0; i < list.Count; i++)
            {
                Resolved r = list[i];
                if (r.End <= _startedAt) continue;                    // 启动前的下课不补发
                if (now < r.End) continue;                            // 还没到点
                if (now > r.End.AddMinutes(2)) continue;              // 迟到太久（如休眠后）不再提醒

                string key = now.Date.ToString("yyyyMMdd") + "|" + r.Item.Start + "|" + r.Item.End + "|" + r.Subject;
                if (_fired.Contains(key)) continue;
                _fired.Add(key);

                Resolved next = null;
                for (int j = i + 1; j < list.Count; j++)
                {
                    if (list[j].Start >= r.End) { next = list[j]; break; }
                }

                string subject = string.IsNullOrEmpty(r.Subject) ? "本节课" : r.Subject;
                string label = (r.Item.Label != null && r.Item.Label.Trim().Length > 0)
                    ? r.Item.Label.Trim() + " "
                    : "";
                string line1 = string.Format("{0}（{1}{2} - {3}）已结束", subject, label, r.Item.Start, r.Item.End);
                string line2 = next != null
                    ? string.Format("下一节：{0}  {1} 开始", next.Subject, next.Item.Start)
                    : "今日课程已上完，辛苦啦喵~";     // 今天最后一节课的下课提示

                if (Fire != null) Fire("下课提醒", line1, line2);
            }
        }
    }

    // =================================================================
    //  开机自启（写入当前用户注册表 Run 项，无需管理员权限）
    // =================================================================

    public static class AutoRunHelper
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "ClassroomAssistant";

        public static bool IsEnabled()
        {
            try
            {
                using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, false))
                {
                    if (k == null) return false;
                    return k.GetValue(ValueName) != null;
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 设置开机自启。返回 null = 成功；返回字符串 = 失败原因。
        /// 注意：这是“附加功能”，失败绝不影响设置本身的保存。
        /// </summary>
        public static string Apply(bool enable)
        {
            try
            {
                using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (k == null) return "设置已保存，但无法访问注册表，开机自启未生效。";
                    if (enable)
                    {
                        string exe = System.Reflection.Assembly.GetEntryAssembly().Location;
                        k.SetValue(ValueName, "\"" + exe + "\"");
                    }
                    else
                    {
                        k.DeleteValue(ValueName, false);
                    }
                    return null;
                }
            }
            catch (Exception ex)
            {
                return enable
                    ? "设置已保存，但「开机自动运行」设置失败：\r\n" + ex.Message +
                      "\r\n（可能是系统策略限制了注册表写入；可手动把程序快捷方式放进 shell:startup 文件夹）"
                    : null;
            }
        }
    }

    // =================================================================
    //  右侧书签：常驻屏幕右缘的“书签”标签，点击即可打开设置
    // =================================================================

    public class BookmarkTab : Window
    {
        public event Action Clicked;
        public event Action ToggleOverlayRequested;
        public event Action HideRequested;
        public event Action ExitRequested;

        private readonly Border _pill;
        private readonly TranslateTransform _slide;
        private readonly Brush _normalBrush = new SolidColorBrush(Color.FromArgb(145, 28, 110, 220));  // 蓝色半透明
        private readonly Brush _hoverBrush = new SolidColorBrush(Color.FromArgb(210, 45, 135, 248));
        private readonly double _restX;                 // 平时缩回的位置（只露一条边）
        private DateTime _lastOpen = DateTime.MinValue;

        public BookmarkTab(AppConfig cfg)
        {
            double scale = cfg != null ? cfg.FontScale : 1.0;

            Title = "课堂助手设置书签";
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = null;                          // 透明部分不挡桌面，只有书签本体可点
            Topmost = true;
            ShowInTaskbar = false;
            ShowActivated = false;
            ResizeMode = ResizeMode.NoResize;
            Focusable = false;
            FontFamily = new FontFamily("Microsoft YaHei");
            Width = 34 * scale;                         // 小巧的书签
            Height = 96 * scale;

            _slide = new TranslateTransform();
            _restX = 20 * scale;                        // 缩回后只在屏幕边缘露出 8px 的小舌头
            _slide.X = _restX;

            // 书签样式：蓝色半透明、左侧圆角，贴着屏幕右缘
            _pill = new Border();
            _pill.Width = Width - 6 * scale;
            _pill.Height = Height - 4 * scale;
            _pill.HorizontalAlignment = HorizontalAlignment.Right;
            _pill.VerticalAlignment = VerticalAlignment.Center;
            _pill.CornerRadius = new CornerRadius(11, 0, 0, 11);
            _pill.Background = _normalBrush;
            _pill.BorderThickness = new Thickness(1, 1, 0, 1);
            _pill.BorderBrush = new SolidColorBrush(Color.FromArgb(80, 255, 255, 255));
            _pill.Effect = WpfUtil.TextShadow(12, 0.5);
            _pill.RenderTransform = _slide;

            StackPanel text = new StackPanel();
            text.Orientation = Orientation.Vertical;
            text.HorizontalAlignment = HorizontalAlignment.Center;
            text.VerticalAlignment = VerticalAlignment.Center;
            foreach (char ch in "设置".ToCharArray())
            {
                TextBlock t = WpfUtil.OverlayText(ch.ToString(), 13 * scale, FontWeights.Bold, 1.0);
                t.Foreground = Brushes.White;      // 书签是蓝色底的 UI，固定白字
                t.HorizontalAlignment = HorizontalAlignment.Center;
                t.Margin = new Thickness(0, 1, 0, 1);
                text.Children.Add(t);
            }
            _pill.Child = text;
            Content = _pill;

            Cursor = Cursors.Hand;
            MouseEnter += OnEnter;
            MouseLeave += OnLeave;
            // 用冒泡的 MouseUp（而不是 Direct 的 MouseLeftButtonUp）：
            // 无论点到标签、文字还是空白处，事件都能到达窗口
            AddHandler(MouseUpEvent, new MouseButtonEventHandler(OnMouseUp), true);

            ContextMenu menu = new ContextMenu();
            menu.FontFamily = FontFamily;
            menu.FontSize = 13;
            menu.Items.Add(MakeMenuItem("打开设置(&S)...", delegate { Raise(Clicked); }));
            menu.Items.Add(MakeMenuItem("隐藏覆盖层(&H)", delegate { Raise(ToggleOverlayRequested); }));
            menu.Items.Add(MakeMenuItem("隐藏书签(&B)", delegate { Raise(HideRequested); }));
            menu.Items.Add(new Separator());
            menu.Items.Add(MakeMenuItem("退出(&X)", delegate { Raise(ExitRequested); }));
            ContextMenu = menu;

            SourceInitialized += OnSourceInitialized;
            Loaded += delegate { FitToScreen(); };
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
            Closed += delegate { Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged; };
        }

        private static MenuItem MakeMenuItem(string header, Action onClick)
        {
            MenuItem item = new MenuItem();
            item.Header = header;
            item.Click += delegate { onClick(); };
            return item;
        }

        private static void Raise(Action handler)
        {
            if (handler != null) handler();
        }

        private void OnSourceInitialized(object sender, EventArgs e)
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            NativeMethods.MakeNonActivating(hwnd);
        }

        private void OnDisplaySettingsChanged(object sender, EventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(FitToScreen));
        }

        private void FitToScreen()
        {
            // 贴紧屏幕右边框，固定在全局右下四分之一位置（右下象限的竖直中心）
            Left = SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - Width;
            Top = SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight * 0.75 - Height / 2;
        }

        private void OnEnter(object sender, MouseEventArgs e)
        {
            // 鼠标靠近才滑出
            _pill.Background = _hoverBrush;
            AnimateSlide(0);
        }

        private void OnLeave(object sender, MouseEventArgs e)
        {
            // 鼠标离开后自动缩回，只在屏幕边缘留一条小舌头
            _pill.Background = _normalBrush;
            AnimateSlide(_restX);
        }

        private void AnimateSlide(double to)
        {
            DoubleAnimation anim = new DoubleAnimation(to, TimeSpan.FromMilliseconds(180));
            anim.EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut };
            _slide.BeginAnimation(TranslateTransform.XProperty, anim);
        }

        private void OnMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left) return;
            e.Handled = true;
            if (DateTime.Now - _lastOpen < TimeSpan.FromMilliseconds(600)) return;
            _lastOpen = DateTime.Now;
            Raise(Clicked);
        }

        /// 自检用：在内部模拟一次左键点击（从标签子元素冒泡上来），验证“点击书签 → 打开设置”这条链路。
        public void SimulateClick()
        {
            MouseButtonEventArgs args = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left);
            args.RoutedEvent = MouseUpEvent;
            _pill.RaiseEvent(args);
        }
    }

    // =================================================================
    //  设置窗口
    // =================================================================

    public class SettingsWindow : Window
    {
        public event Action<AppConfig> Saved;

        private AppConfig _edit;
        private DataGrid _eventsGrid;
        private DataGrid _grid;
        private Slider _fontSlider;
        private Slider _dimSlider;
        private CheckBox _toastPanelCheck;
        private CheckBox _autoRunCheck;
        private CheckBox _bookmarkCheck;
        private CheckBox _textTopmostCheck;
        private ComboBox _styleBox;
        private TextBlock _fontValueText;
        private TextBlock _dimValueText;
        private TextBlock _preview;
        private bool _forceClose;
        private bool _loading;

        public SettingsWindow()
        {
            Title = "课堂助手 - 设置";
            Width = 880;
            Height = 800;
            MinWidth = 700;
            MinHeight = 560;
            Topmost = true;                 // 设置界面全局置顶
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            FontFamily = new FontFamily("Microsoft YaHei");
            Background = new SolidColorBrush(Color.FromRgb(0xEE, 0xF1, 0xF6));

            Grid root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                    // 标题栏
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // 内容
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                    // 底部按钮

            // 彩色标题栏
            StackPanel headerText = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(22, 13, 22, 13) };
            headerText.Children.Add(new TextBlock
            {
                Text = "课堂助手 · 设置",
                FontSize = 19,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White
            });
            headerText.Children.Add(new TextBlock
            {
                Text = "倒计时 · 今日课程 · 下课提醒 · 桌面层显示",
                FontSize = 11.5,
                Foreground = new SolidColorBrush(Color.FromRgb(0xD9, 0xE4, 0xFF)),
                Margin = new Thickness(0, 3, 0, 0)
            });
            Border header = new Border { Background = Accent, Child = headerText };
            Grid.SetRow(header, 0);
            root.Children.Add(header);

            ScrollViewer scroll = new ScrollViewer();
            scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;

            StackPanel stack = new StackPanel();
            stack.Orientation = Orientation.Vertical;

            // ---------- 倒计时卡片 ----------
            _eventsGrid = new DataGrid
            {
                Height = 168,
                AutoGenerateColumns = false,
                CanUserAddRows = false,
                CanUserDeleteRows = false,
                SelectionMode = DataGridSelectionMode.Single,
                GridLinesVisibility = DataGridGridLinesVisibility.All,
                HeadersVisibility = DataGridHeadersVisibility.Column,
                RowHeight = 30,
                FontSize = 13
            };
            _eventsGrid.Columns.Add(new DataGridCheckBoxColumn
            {
                Header = "显示",
                Width = new DataGridLength(52),
                Binding = new System.Windows.Data.Binding("Enabled")
            });
            _eventsGrid.Columns.Add(TextColumn("事件名称", "Name", 150));
            _eventsGrid.Columns.Add(TextColumn("目标日期", "Date", 110));
            _eventsGrid.Columns.Add(TextColumn("背景图片（本地文件，可留空）", "Image", 270));

            StackPanel eventBtns = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            eventBtns.Children.Add(ButtonOf("添加事件", OnAddEvent));
            eventBtns.Children.Add(ButtonOf("删除选中", OnDeleteEvent));
            eventBtns.Children.Add(ButtonOf("选择背景图片…", OnPickImage));
            eventBtns.Children.Add(ButtonOf("清除背景", OnClearImage));

            _preview = new TextBlock
            {
                FontSize = 26,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                Margin = new Thickness(0, 0, 0, 0)
            };
            // 预览区：深色底模拟桌面，所见即所得
            Border previewBg = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x39, 0x41, 0x4F)),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(18, 14, 18, 14),
                Margin = new Thickness(0, 12, 0, 0),
                Child = _preview
            };

            StyleGrid(_eventsGrid);
            stack.Children.Add(Card("倒计时卡片（可添加多个事件，类似“倒数日”）",
                _eventsGrid,
                eventBtns,
                Hint("目标日期写 2027-06-07 这样的格式；“显示”列打勾的卡片才会出现在桌面顶部。" +
                     "背景图片留空 = 纯文字（不遮挡桌面）；选择本地图片后显示为横版图片卡片。"),
                previewBg));

            // ---------- 一周课程表 ----------
            _grid = new DataGrid
            {
                Height = 360,
                AutoGenerateColumns = false,
                CanUserAddRows = false,
                CanUserDeleteRows = false,
                SelectionMode = DataGridSelectionMode.Single,
                GridLinesVisibility = DataGridGridLinesVisibility.All,
                HeadersVisibility = DataGridHeadersVisibility.Column,
                RowHeight = 32,
                FontSize = 13
            };
            _grid.Columns.Add(TextColumn("节次", "Label", 92));
            _grid.Columns.Add(TextColumn("开始", "Start", 72));
            _grid.Columns.Add(TextColumn("结束", "End", 72));
            string[] dayNames = new string[] { "周一", "周二", "周三", "周四", "周五", "周六", "周日" };
            string[] dayProps = new string[] { "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun" };
            for (int i = 0; i < 7; i++)
                _grid.Columns.Add(TextColumn(dayNames[i], dayProps[i], 84));

            Button addBtn = ButtonOf("添加节次", OnAddCourse);
            Button delBtn = ButtonOf("删除选中节次", OnDeleteCourse);
            StackPanel gridBtns = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            gridBtns.Children.Add(addBtn);
            gridBtns.Children.Add(delBtn);

            StyleGrid(_grid);
            stack.Children.Add(Card("一周课程表（直接点击单元格即可修改）",
                _grid,
                gridBtns,
                Hint("一行 = 一个节次：左边填节次名、开始和结束时间，右边七列填周一到周日上的科目；留空表示当天没有这节课。" +
                     "桌面右侧只显示科目名，已上完的自动变暗；每节课的结束时间就是下课提醒的触发时间。")));

            // ---------- 外观 ----------
            _fontSlider = new Slider
            {
                Minimum = 0.7,
                Maximum = 1.8,
                TickFrequency = 0.05,
                IsSnapToTickEnabled = true,
                Width = 260,
                Height = 28,
                VerticalAlignment = VerticalAlignment.Center
            };
            _fontValueText = TextOf("100%", 13, FontWeights.Normal, VerticalAlignment.Center);
            StackPanel fontRow = new StackPanel { Orientation = Orientation.Horizontal };
            fontRow.Children.Add(TextOf("字体大小", 13.5, FontWeights.Normal, VerticalAlignment.Center));
            fontRow.Children.Add(_fontSlider);
            fontRow.Children.Add(_fontValueText);

            _dimSlider = new Slider
            {
                Minimum = 0.08,
                Maximum = 0.5,
                TickFrequency = 0.02,
                IsSnapToTickEnabled = true,
                Width = 260,
                Height = 28,
                VerticalAlignment = VerticalAlignment.Center
            };
            _dimValueText = TextOf("25%", 13, FontWeights.Normal, VerticalAlignment.Center);
            StackPanel dimRow = new StackPanel { Orientation = Orientation.Horizontal };
            dimRow.Children.Add(TextOf("已上完课程亮度", 13.5, FontWeights.Normal, VerticalAlignment.Center));
            dimRow.Children.Add(_dimSlider);
            dimRow.Children.Add(_dimValueText);

            // 文字样式（保留两个版本：暖阳金 / 经典白）
            _styleBox = new ComboBox { Width = 270, Height = 32, VerticalAlignment = VerticalAlignment.Center };
            _styleBox.Items.Add(new ComboBoxItem { Content = "暖阳金 · 金橙渐变（推荐）", Tag = "gold" });
            _styleBox.Items.Add(new ComboBoxItem { Content = "经典白 · 纯白文字", Tag = "white" });
            _styleBox.SelectionChanged += delegate { if (!_loading) UpdatePreview(); };
            StackPanel styleRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
            styleRow.Children.Add(TextOf("文字样式", 13.5, FontWeights.Normal, VerticalAlignment.Center));
            styleRow.Children.Add(_styleBox);

            _toastPanelCheck = new CheckBox
            {
                Content = "下课提示使用半透明背景（默认纯文字）",
                Margin = new Thickness(0, 6, 0, 0),
                FontSize = 13.5
            };
            _textTopmostCheck = new CheckBox
            {
                Content = "文字全局置顶（默认：文字位于桌面之上、应用之下）",
                Margin = new Thickness(0, 10, 0, 0),
                FontSize = 13.5
            };

            stack.Children.Add(Card("外观",
                styleRow, fontRow, dimRow, _toastPanelCheck, _textTopmostCheck));

            // ---------- 其他 ----------
            _autoRunCheck = new CheckBox
            {
                Content = "开机自动运行（写入当前用户的启动项）",
                Margin = new Thickness(0, 4, 0, 0),
                FontSize = 13.5
            };
            _bookmarkCheck = new CheckBox
            {
                Content = "在屏幕右侧显示「设置」书签（点击书签即可打开本设置）",
                Margin = new Thickness(0, 10, 0, 0),
                FontSize = 13.5
            };
            stack.Children.Add(Card("其他",
                _autoRunCheck,
                _bookmarkCheck,
                Hint("打开设置的两个入口：右下角托盘图标（双击或右键 → 设置），或屏幕右缘的「设置」书签。托盘菜单里还能重新加载配置、隐藏/显示覆盖层或退出程序。")));

            scroll.Content = stack;
            Border body = new Border();
            body.Padding = new Thickness(20, 16, 20, 4);
            body.Child = scroll;
            Grid.SetRow(body, 1);
            root.Children.Add(body);

            // ---------- 底部按钮 ----------
            StackPanel buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            Button saveBtn = ButtonOf("保 存", OnSave, true);
            saveBtn.Width = 132;
            saveBtn.Height = 42;
            saveBtn.FontSize = 15;
            Button cancelBtn = ButtonOf("取 消", OnCancel);
            cancelBtn.Width = 112;
            cancelBtn.Height = 42;
            cancelBtn.Margin = new Thickness(8, 0, 0, 0);
            buttons.Children.Add(saveBtn);
            buttons.Children.Add(cancelBtn);
            Border footer = new Border();
            footer.Background = Brushes.White;
            footer.Padding = new Thickness(20, 12, 20, 14);
            footer.BorderBrush = new SolidColorBrush(Color.FromRgb(0xDD, 0xE3, 0xEC));
            footer.BorderThickness = new Thickness(0, 1, 0, 0);
            footer.Child = buttons;
            Grid.SetRow(footer, 2);
            root.Children.Add(footer);

            Content = root;

            // 事件最后绑定，避免初始化时触发空引用
            _fontSlider.ValueChanged += delegate
            {
                if (_fontValueText != null) _fontValueText.Text = string.Format("{0}%", Math.Round(_fontSlider.Value * 100));
                if (!_loading) UpdatePreview();
            };
            _dimSlider.ValueChanged += delegate
            {
                if (_dimValueText != null) _dimValueText.Text = string.Format("{0}%", Math.Round(_dimSlider.Value * 100));
            };

            Closing += delegate(object s, System.ComponentModel.CancelEventArgs e)
            {
                if (!_forceClose) { e.Cancel = true; Hide(); }
            };
        }

        // ---------- 打开 / 保存 ----------

        public void Open(AppConfig current)
        {
            _loading = true;
            _edit = AppConfig.Clone(current);

            _fontSlider.Value = _edit.FontScale;
            _dimSlider.Value = _edit.DimOpacity;
            _styleBox.SelectedIndex = string.Equals(_edit.TextStyle, "white", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            _toastPanelCheck.IsChecked = _edit.ToastPanel;
            _bookmarkCheck.IsChecked = _edit.ShowBookmark == true;
            _textTopmostCheck.IsChecked = _edit.TextTopmost == true;
            _autoRunCheck.IsChecked = AutoRunHelper.IsEnabled();

            BindEventsGrid();
            _grid.ItemsSource = null;
            _grid.ItemsSource = _edit.Periods;

            _loading = false;
            UpdatePreview();
            Show();
            Activate();
            Focus();
        }

        public void ForceClose()
        {
            _forceClose = true;
            Close();
        }

        /// 自检/排障用：把设置界面渲染成 PNG 快照，方便核对布局。
        public void SaveSnapshot(string path)
        {
            UpdateLayout();
            int w = Math.Max(1, (int)Math.Ceiling(ActualWidth));
            int h = Math.Max(1, (int)Math.Ceiling(ActualHeight));
            RenderTargetBitmap rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(this);
            PngBitmapEncoder png = new PngBitmapEncoder();
            png.Frames.Add(BitmapFrame.Create(rtb));
            using (FileStream fs = File.Create(path))
            {
                png.Save(fs);
            }
        }

        private void OnSave(object sender, RoutedEventArgs e)
        {
            string note;
            bool ok = Save(out note);
            if (note != null) Notice.Show(this, note, ok ? MessageBoxImage.Warning : MessageBoxImage.Error);
        }

        /// <summary>
        /// 保存设置（保存按钮与自检共用同一条链路）。
        /// 本方法不弹任何对话框：false = 有错误且未保存；note = 需要提示用户的内容（可为 null）。
        /// </summary>
        public bool Save(out string note)
        {
            note = null;
            _edit.FontScale = _fontSlider.Value;
            _edit.DimOpacity = _dimSlider.Value;
            _edit.ToastPanel = _toastPanelCheck.IsChecked == true;
            _edit.ShowBookmark = _bookmarkCheck.IsChecked == true;
            _edit.TextTopmost = _textTopmostCheck.IsChecked == true;
            _edit.TextStyle = SelectedStyle();

            // 清理倒计时卡片：校验日期格式
            List<EventItem> keptEvents = new List<EventItem>();
            foreach (EventItem ev in _edit.Events)
            {
                if (ev == null) continue;
                DateTime evDate;
                if (!ev.TryGetDate(out evDate))
                {
                    note = string.Format("事件「{0}」的目标日期格式不正确，请写成 2027-06-07 这样的格式。",
                        (ev.Name == null || ev.Name.Trim().Length == 0) ? "未命名" : ev.Name.Trim());
                    return false;
                }
                ev.Name = ev.Name == null ? "" : ev.Name.Trim();
                ev.Date = evDate.ToString("yyyy-MM-dd");
                ev.Image = ev.Image == null ? "" : ev.Image.Trim();
                keptEvents.Add(ev);
            }
            _edit.Events = keptEvents;
            BindEventsGrid();

            // 清理课程表：整行全空的节次删掉，校验时间格式，最后按开始时间排序
            List<PeriodItem> cleaned = new List<PeriodItem>();
            foreach (PeriodItem p in _edit.Periods)
            {
                if (p == null) continue;
                string label = p.Label == null ? "" : p.Label.Trim();
                string[] subjects = new string[8];
                bool any = label.Length > 0;
                for (int day = 1; day <= 7; day++)
                {
                    string s = p.GetSubject(day);
                    s = s == null ? "" : s.Trim();
                    subjects[day] = s;
                    if (s.Length > 0) any = true;
                }
                if (!any) continue;                     // 整行都没填：当作删除

                TimeSpan t1, t2;
                bool ok1 = TimeUtil.TryParse(p.Start, out t1);
                bool ok2 = TimeUtil.TryParse(p.End, out t2);
                if (!ok1 || !ok2)
                {
                    note = string.Format("节次「{0}」的时间格式不正确，请填写如 08:00 这样的格式。",
                        label.Length > 0 ? label : "未命名");
                    return false;
                }

                PeriodItem n = new PeriodItem();
                n.Label = label;
                n.Start = p.Start.Trim();
                n.End = p.End.Trim();
                for (int day = 1; day <= 7; day++) n.SetSubject(day, subjects[day]);
                cleaned.Add(n);
            }
            cleaned.Sort(delegate(PeriodItem a, PeriodItem b)
            {
                TimeSpan ta, tb;
                TimeUtil.TryParse(a.Start, out ta);
                TimeUtil.TryParse(b.Start, out tb);
                return ta.CompareTo(tb);
            });
            _edit.Periods = cleaned;
            _grid.ItemsSource = null;
            _grid.ItemsSource = _edit.Periods;

            try
            {
                ConfigStore.Save(_edit);
            }
            catch (Exception ex)
            {
                note = "保存设置失败：" + ex.Message +
                    "\r\n\r\n如果程序装在只读位置（如 Program Files），请把整个文件夹移到 " +
                    "D:\\classroom-assistant 这类可写目录后重试。";
                return false;
            }

            // 开机自启是附加功能：失败绝不影响设置保存
            note = AutoRunHelper.Apply(_autoRunCheck.IsChecked == true);

            if (Saved != null) Saved(_edit);
            Hide();
            return true;
        }

        private void OnCancel(object sender, RoutedEventArgs e)
        {
            Hide();
        }

        // ---------- 课程表编辑 ----------

        private void OnAddCourse(object sender, RoutedEventArgs e)
        {
            PeriodItem p = new PeriodItem();
            p.Label = "新节次";
            p.Start = "08:00";
            p.End = "08:45";
            for (int day = 1; day <= 7; day++) p.SetSubject(day, "");
            _edit.Periods.Add(p);
            _grid.ItemsSource = null;
            _grid.ItemsSource = _edit.Periods;
        }

        private void OnDeleteCourse(object sender, RoutedEventArgs e)
        {
            PeriodItem sel = _grid.SelectedItem as PeriodItem;
            if (sel == null)
            {
                Notice.Show(this, "请先在课程表里选中一行节次。", MessageBoxImage.Information);
                return;
            }
            _edit.Periods.Remove(sel);
            _grid.ItemsSource = null;
            _grid.ItemsSource = _edit.Periods;
        }

        private string SelectedStyle()
        {
            ComboBoxItem item = _styleBox.SelectedItem as ComboBoxItem;
            return (item != null && string.Equals(item.Tag as string, "white", StringComparison.OrdinalIgnoreCase))
                ? "white" : "gold";
        }

        /// 自检用：直接切换文字样式下拉框（等同于用户选择）。
        public void SetStyleSelection(bool classicWhite)
        {
            _styleBox.SelectedIndex = classicWhite ? 1 : 0;
        }

        private void UpdatePreview()
        {
            if (_preview == null || _edit == null) return;
            WpfUtil.SetTextStyle(SelectedStyle());   // 预览跟随所选文字样式
            _preview.Foreground = WpfUtil.MainBrush();
            _preview.FontSize = 22 * _fontSlider.Value;
            StringBuilder sb = new StringBuilder();
            foreach (EventItem ev in _edit.Events)
            {
                if (ev == null || !ev.Enabled) continue;
                DateTime d;
                if (!ev.TryGetDate(out d)) continue;
                int days = (d.Date - DateTime.Today).Days;
                string name = (ev.Name == null || ev.Name.Trim().Length == 0) ? "倒计时" : ev.Name.Trim();
                if (sb.Length > 0) sb.Append("　｜　");
                if (days > 0) sb.AppendFormat("{0} {1} 天", name, days);
                else if (days == 0) sb.AppendFormat("{0} 就是今天", name);
                else sb.AppendFormat("{0} 已过 {1} 天", name, -days);
            }
            _preview.Text = sb.Length > 0 ? sb.ToString() : "（没有启用的倒计时卡片）";
        }

        // ---------- 倒计时卡片编辑 ----------

        private void BindEventsGrid()
        {
            if (_edit == null) return;
            _eventsGrid.ItemsSource = null;
            _eventsGrid.ItemsSource = _edit.Events;
            UpdatePreview();
        }

        private void OnAddEvent(object sender, RoutedEventArgs e)
        {
            EventItem ev = new EventItem();
            ev.Name = "新事件";
            ev.Date = DateTime.Today.AddMonths(1).ToString("yyyy-MM-dd");
            ev.Image = "";
            ev.Enabled = true;
            _edit.Events.Add(ev);
            BindEventsGrid();
        }

        private void OnDeleteEvent(object sender, RoutedEventArgs e)
        {
            EventItem sel = _eventsGrid.SelectedItem as EventItem;
            if (sel == null)
            {
                Notice.Show(this, "请先在卡片列表里选中一行。", MessageBoxImage.Information);
                return;
            }
            _edit.Events.Remove(sel);
            BindEventsGrid();
        }

        private void OnPickImage(object sender, RoutedEventArgs e)
        {
            EventItem sel = _eventsGrid.SelectedItem as EventItem;
            if (sel == null)
            {
                Notice.Show(this, "请先选中一个事件，再给它设置背景图片。", MessageBoxImage.Information);
                return;
            }
            Microsoft.Win32.OpenFileDialog dlg = new Microsoft.Win32.OpenFileDialog();
            dlg.Title = "选择卡片背景图片";
            dlg.Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.gif|所有文件|*.*";
            if (dlg.ShowDialog() == true)
            {
                sel.Image = dlg.FileName;
                BindEventsGrid();
            }
        }

        private void OnClearImage(object sender, RoutedEventArgs e)
        {
            EventItem sel = _eventsGrid.SelectedItem as EventItem;
            if (sel == null) return;
            sel.Image = "";
            BindEventsGrid();
        }

        // ---------- 设置界面样式 ----------

        private static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(0x2F, 0x6F, 0xE4));
        private static readonly Brush AccentSoft = new SolidColorBrush(Color.FromRgb(0xEC, 0xF2, 0xFE));
        private static readonly Brush AccentBorder = new SolidColorBrush(Color.FromRgb(0xC4, 0xD5, 0xF7));
        private static readonly Brush TextDark = new SolidColorBrush(Color.FromRgb(0x21, 0x29, 0x36));
        private static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80));

        /// 白色圆角卡片：一组设置项（标题 + 内容）。
        private static UIElement Card(string title, params UIElement[] children)
        {
            StackPanel body = new StackPanel();
            body.Orientation = Orientation.Vertical;

            StackPanel titleRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
            titleRow.Children.Add(new Border
            {
                Width = 5,
                Height = 20,
                CornerRadius = new CornerRadius(3),
                Background = Accent,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0)
            });
            titleRow.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 15.5,
                FontWeight = FontWeights.Bold,
                Foreground = TextDark,
                VerticalAlignment = VerticalAlignment.Center
            });
            body.Children.Add(titleRow);
            foreach (UIElement e in children) body.Children.Add(e);

            Border card = new Border();
            card.Background = Brushes.White;
            card.CornerRadius = new CornerRadius(12);
            card.Padding = new Thickness(18, 16, 18, 18);
            card.Margin = new Thickness(0, 0, 0, 16);
            card.Effect = new DropShadowEffect
            {
                Color = Colors.Black,
                BlurRadius = 16,
                ShadowDepth = 2,
                Direction = 270,
                Opacity = 0.10
            };
            card.Child = body;
            return card;
        }

        /// 表格美化：浅蓝表头、隔行底色、细分隔线。
        private static void StyleGrid(DataGrid grid)
        {
            grid.Background = Brushes.White;
            grid.RowBackground = Brushes.White;
            grid.AlternatingRowBackground = new SolidColorBrush(Color.FromRgb(0xF6, 0xF8, 0xFC));
            grid.HorizontalGridLinesBrush = new SolidColorBrush(Color.FromRgb(0xE4, 0xE8, 0xEF));
            grid.VerticalGridLinesBrush = new SolidColorBrush(Color.FromRgb(0xE4, 0xE8, 0xEF));
            grid.BorderBrush = new SolidColorBrush(Color.FromRgb(0xDD, 0xE3, 0xEC));
            grid.BorderThickness = new Thickness(1);

            Style header = new Style(typeof(DataGridColumnHeader));
            header.Setters.Add(new Setter(Control.BackgroundProperty, AccentSoft));
            header.Setters.Add(new Setter(Control.ForegroundProperty, new SolidColorBrush(Color.FromRgb(0x2A, 0x4B, 0x8D))));
            header.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.Bold));
            header.Setters.Add(new Setter(Control.FontSizeProperty, 13.0));
            header.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(6, 7, 6, 7)));
            header.Setters.Add(new Setter(Control.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(0xDD, 0xE3, 0xEC))));
            header.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0, 0, 1, 1)));
            grid.ColumnHeaderStyle = header;
        }

        private static DataGridTextColumn TextColumn(string header, string path, double width)
        {
            DataGridTextColumn c = new DataGridTextColumn();
            c.Header = header;
            c.Width = new DataGridLength(width);
            c.Binding = new System.Windows.Data.Binding(path);
            return c;
        }

        private static TextBlock TextOf(string text, double size, FontWeight weight, VerticalAlignment va)
        {
            return new TextBlock
            {
                Text = text,
                FontSize = size,
                FontWeight = weight,
                Foreground = TextDark,
                VerticalAlignment = va,
                Margin = new Thickness(0, 0, 12, 0)
            };
        }

        private static TextBlock Hint(string text)
        {
            return new TextBlock
            {
                Text = text,
                FontSize = 12,
                Foreground = Muted,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 10, 0, 0),
                LineHeight = 20
            };
        }

        /// 圆角按钮：primary = 主色实心（保存），否则浅色描边。
        private static Button ButtonOf(string text, RoutedEventHandler onClick)
        {
            return ButtonOf(text, onClick, false);
        }

        private static Button ButtonOf(string text, RoutedEventHandler onClick, bool primary)
        {
            Button b = new Button();
            b.Content = text;
            b.Margin = new Thickness(0, 0, 10, 0);
            b.Foreground = primary ? Brushes.White : Accent;
            b.FontWeight = FontWeights.Bold;
            b.FontSize = 13.5;
            b.Cursor = Cursors.Hand;

            FrameworkElementFactory border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(9));
            border.SetValue(Border.BackgroundProperty, primary ? Accent : (Brush)AccentSoft);
            border.SetValue(Border.BorderBrushProperty, primary ? (Brush)Brushes.Transparent : AccentBorder);
            border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            border.SetValue(Border.PaddingProperty, new Thickness(18, 9, 18, 9));
            FrameworkElementFactory cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(cp);
            ControlTemplate tpl = new ControlTemplate(typeof(Button));
            tpl.VisualTree = border;
            b.Template = tpl;

            b.Click += onClick;
            return b;
        }
    }

    // =================================================================
    //  托盘图标
    // =================================================================

    public class TrayController : IDisposable
    {
        public event Action OpenSettingsRequested;
        public event Action ReloadRequested;
        public event Action ToggleRequested;
        public event Action RefreshRequested;
        public event Action ExitRequested;

        private Forms.NotifyIcon _icon;
        private Forms.ToolStripMenuItem _visibilityItem;
        private System.IO.MemoryStream _iconStream;

        public TrayController()
        {
            Forms.ContextMenuStrip menu = new Forms.ContextMenuStrip();
            menu.Font = new Drawing.Font("Microsoft YaHei", 9.5f);

            Forms.ToolStripMenuItem openItem = new Forms.ToolStripMenuItem("设置(&S)...");
            openItem.Font = new Drawing.Font("Microsoft YaHei", 9.5f, Drawing.FontStyle.Bold);
            openItem.Click += delegate { Raise(OpenSettingsRequested); };
            menu.Items.Add(openItem);

            _visibilityItem = new Forms.ToolStripMenuItem("隐藏覆盖层(&H)");
            _visibilityItem.Click += delegate { Raise(ToggleRequested); };
            menu.Items.Add(_visibilityItem);

            Forms.ToolStripMenuItem refreshItem = new Forms.ToolStripMenuItem("立即刷新(&R)");
            refreshItem.Click += delegate { Raise(RefreshRequested); };
            menu.Items.Add(refreshItem);

            Forms.ToolStripMenuItem reloadItem = new Forms.ToolStripMenuItem("重新加载配置(&L)");
            reloadItem.Click += delegate { Raise(ReloadRequested); };
            menu.Items.Add(reloadItem);

            menu.Items.Add(new Forms.ToolStripSeparator());

            Forms.ToolStripMenuItem exitItem = new Forms.ToolStripMenuItem("退出(&X)");
            exitItem.Click += delegate { Raise(ExitRequested); };
            menu.Items.Add(exitItem);

            _icon = new Forms.NotifyIcon();
            _icon.Icon = CreateIcon();
            _icon.Text = "课堂助手：倒计时 + 今日课程 + 下课提醒";
            _icon.ContextMenuStrip = menu;
            _icon.Visible = true;
            _icon.DoubleClick += delegate { Raise(OpenSettingsRequested); };
        }

        public void SetOverlayVisible(bool visible)
        {
            if (_visibilityItem != null)
                _visibilityItem.Text = visible ? "隐藏覆盖层(&H)" : "显示覆盖层(&H)";
        }

        private static void Raise(Action handler)
        {
            if (handler != null) handler();
        }

        /// 更新托盘悬停提示：显示运行状态（如“运行中｜高考 250 天”）。
        public void SetStatus(string status)
        {
            if (_icon == null) return;
            string text = status == null ? "" : status.Trim();
            if (text.Length == 0) text = "课堂助手 · 运行中";
            if (text.Length > 62) text = text.Substring(0, 62);   // 系统对提示长度有限制
            _icon.Text = text;
        }

        /// <summary>
        /// 生成托盘图标：先按 PNG 条目打包成 .ico（透明边缘正确、Win11 托盘显示正常），
        /// 失败时退回 GDI 直接生成的不透明图标。
        /// </summary>
        private Drawing.Icon CreateIcon()
        {
            Drawing.Bitmap bmp = RenderIconBitmap(32);
            try
            {
                byte[] png;
                using (System.IO.MemoryStream msPng = new System.IO.MemoryStream())
                {
                    bmp.Save(msPng, System.Drawing.Imaging.ImageFormat.Png);
                    png = msPng.ToArray();
                }
                _iconStream = new System.IO.MemoryStream();
                System.IO.BinaryWriter bw = new System.IO.BinaryWriter(_iconStream);
                bw.Write((ushort)0);            // 保留
                bw.Write((ushort)1);            // 类型：图标
                bw.Write((ushort)1);            // 数量
                bw.Write((byte)32);             // 宽
                bw.Write((byte)32);             // 高
                bw.Write((byte)0);              // 调色板数
                bw.Write((byte)0);              // 保留
                bw.Write((ushort)1);            // 色平面
                bw.Write((ushort)32);           // 位深
                bw.Write((uint)png.Length);     // PNG 数据长度
                bw.Write((uint)(6 + 16));       // PNG 数据偏移
                bw.Write(png);
                _iconStream.Position = 0;
                return new Drawing.Icon(_iconStream);
            }
            catch
            {
                return Drawing.Icon.FromHandle(RenderIconBitmap(32).GetHicon());
            }
            finally
            {
                bmp.Dispose();
            }
        }

        private static Drawing.Bitmap RenderIconBitmap(int size)
        {
            Drawing.Bitmap bmp = new Drawing.Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (Drawing.Graphics g = Drawing.Graphics.FromImage(bmp))
            {
                g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.Clear(Drawing.Color.Transparent);
                using (Drawing.Brush b = new Drawing.SolidBrush(Drawing.Color.FromArgb(255, 38, 110, 215)))
                {
                    g.FillEllipse(b, 0, 0, size, size);
                }
                using (Drawing.Font f = new Drawing.Font("Microsoft YaHei", size * 0.48f,
                    Drawing.FontStyle.Bold, Drawing.GraphicsUnit.Pixel))
                {
                    string ch = "学";
                    Drawing.SizeF sz = g.MeasureString(ch, f);
                    g.DrawString(ch, f, Drawing.Brushes.White,
                        (size - sz.Width) / 2f, (size - sz.Height) / 2f);
                }
            }
            return bmp;
        }

        public void Dispose()
        {
            if (_icon != null)
            {
                _icon.Visible = false;
                _icon.Dispose();
                _icon = null;
            }
            if (_iconStream != null)
            {
                _iconStream.Dispose();
                _iconStream = null;
            }
        }
    }

    // =================================================================
    //  外壳：把各部分组装起来
    // =================================================================

    public class Shell
    {
        private AppConfig _cfg;
        private OverlayWindow _overlay;
        private ToastWindow _toast;
        private BookmarkTab _bookmark;
        private SettingsWindow _settings;
        private ReminderService _reminder;
        private TrayController _tray;
        private DispatcherTimer _statusTimer;
        private bool _overlayVisible = true;

        public void Start(bool selfTest)
        {
            StringBuilder trace = new StringBuilder();
            Action<string> step = delegate(string msg)
            {
                trace.AppendLine(DateTime.Now.ToString("HH:mm:ss.fff") + "  " + msg);
                if (selfTest) Program.WriteSelfTestLog(trace.ToString());
            };
            try
            {
                step("args/selftest=" + selfTest);
                _cfg = ConfigStore.Load();
                step("config loaded: events=" + _cfg.Events.Count + " periods=" + _cfg.Periods.Count);

                _overlay = new OverlayWindow(_cfg);
                _overlay.Died += RecreateOverlay;
                step("overlay constructed");
                _toast = new ToastWindow(_cfg);
                step("toast constructed");
                _bookmark = new BookmarkTab(_cfg);
                _bookmark.Clicked += OpenSettings;
                _bookmark.ToggleOverlayRequested += ToggleOverlay;
                _bookmark.HideRequested += HideBookmark;
                _bookmark.ExitRequested += Exit;
                step("bookmark constructed");
                _settings = new SettingsWindow();
                _settings.Saved += OnSettingsSaved;
                step("settings constructed");

                _reminder = new ReminderService(_cfg);
                _reminder.Fire += OnReminderFire;
                step("reminder constructed");

                _tray = new TrayController();
                _tray.OpenSettingsRequested += OpenSettings;
                _tray.ReloadRequested += ReloadConfig;
                _tray.ToggleRequested += ToggleOverlay;
                _tray.RefreshRequested += delegate { _overlay.Refresh(); };
                _tray.ExitRequested += Exit;
                _tray.SetOverlayVisible(true);
                step("tray constructed");

                if (selfTest)
                {
                    // 自检：只构建界面与配置读写，不显示任何窗口
                    _overlay.Refresh();
                    step("overlay refreshed, rows=" + _overlay.BuiltRows);

                    // 自检：核对课程状态判定（已上完→变暗 / 进行中 / 未上）
                    DateTime now = DateTime.Now;
                    List<Resolved> today = ScheduleUtil.ForDay(_cfg, now);
                    int fin = 0, cur = 0, up = 0;
                    foreach (Resolved r in today)
                    {
                        CourseState st = ScheduleUtil.GetState(r, now);
                        if (st == CourseState.Finished) fin++;
                        else if (st == CourseState.Current) cur++;
                        else up++;
                    }
                    step("states: finished=" + fin + " current=" + cur + " upcoming=" + up);
                    _overlay.Show();                       // 快照需要窗口至少显示过一次
                    _overlay.SaveSnapshot(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "selftest-overlay.png"));
                    step("desktop layer: " + _overlay.DesktopLayerStatus);
                    _overlay.Hide();
                    step("overlay snapshot saved");

                    AppConfig clone = AppConfig.Clone(_cfg);
                    JavaScriptSerializer js = new JavaScriptSerializer();
                    js.MaxJsonLength = int.MaxValue;
                    AppConfig again = js.Deserialize<AppConfig>(js.Serialize(clone));
                    step("json roundtrip ok: events=" + again.Events.Count + " periods=" + again.Periods.Count);
                    step("showBookmark=" + again.ShowBookmark + " toastPanel=" + again.ToastPanel
                         + " textTopmost=" + again.TextTopmost);
                    step("cards=" + string.Join(",", again.Events.Select(x => x.Name + "(" + x.Date + ")"
                         + (x.Image != null && x.Image.Length > 0 ? "+img" : "")).ToArray()));
                    step("periods=" + again.Periods.Count
                         + " today=" + ScheduleUtil.ForDay(_cfg, DateTime.Now).Count
                         + " migrated=" + ConfigStore.WasMigrated);
                    step("configFile=" + ConfigStore.FilePath);

                    // 自检：模拟点击书签，确认“点击书签 → 打开设置”链路可用
                    try
                    {
                        _bookmark.SimulateClick();
                        step("bookmark click simulated, settingsVisible=" + _settings.IsVisible);
                        _settings.SaveSnapshot(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "selftest-settings.png"));
                        step("settings snapshot saved");

                        // 自检：完整走一遍“在设置里修改 → 保存 → 重新读取”链路
                        AppConfig original = AppConfig.Clone(_cfg);
                        _settings.SetStyleSelection(original.TextStyle != "white");   // 切到另一个文字样式
                        string note;
                        bool saved = _settings.Save(out note);
                        AppConfig reloaded = ConfigStore.Load();
                        step("settings save ok=" + saved + " roundtrip ok=" + (reloaded.TextStyle != original.TextStyle)
                             + " (" + original.TextStyle + " -> " + reloaded.TextStyle + ")"
                             + (note != null ? " note=" + note.Replace("\r\n", " ").Replace("\n", " ") : ""));
                        ConfigStore.Save(original);      // 还原原配置
                        _cfg = original;
                        step("config restored: " + _cfg.TextStyle);

                        _settings.ForceClose();
                    }
                    catch (Exception clickEx)
                    {
                        step("bookmark click simulation failed: " + clickEx.Message);
                    }
                    step("SELFTEST OK");
                    return;
                }

                _overlay.Start();
                _overlay.SetDesktopVisible(true);   // 桌面层显示：文字位于桌面之上、应用之下
                step("overlay shown");
                ApplyBookmark();
                UpdateTrayStatus();
                StartStatusTimer();
                step("bookmark applied");
                _reminder.Start();
                step("running");

                // 提示框放在界面就绪之后，避免模态框挡住界面创建
                if (!selfTest)
                {
                    WarnConfigError();
                    WarnMigration();
                }
            }
            catch (Exception ex)
            {
                step("EXCEPTION: " + ex);
                throw;
            }
            finally
            {
                if (selfTest) Program.WriteSelfTestLog(trace.ToString());
            }
        }

        private void WarnConfigError()
        {
            if (ConfigStore.LastError == null) return;
            Notice.Show(
                "配置文件 config.json 读取失败，本次已使用默认配置。\r\n\r\n原因：" +
                ConfigStore.LastError +
                "\r\n\r\n请检查该文件的格式（应为标准 JSON），或在托盘图标 → 设置 里重新保存一次。",
                MessageBoxImage.Warning);
        }

        private void WarnMigration()
        {
            if (!ConfigStore.WasMigrated) return;
            Notice.Show(
                "已把旧版课程表自动转换为新的整周课程表。\n\n请在设置里核对一遍节次、时间和各天科目，然后点保存即可。",
                MessageBoxImage.Information);
        }

        private void WarnSaveNotice()
        {
            if (ConfigStore.SaveNotice == null) return;
            Notice.Show(ConfigStore.SaveNotice, MessageBoxImage.Warning);
        }

        /// 覆盖层窗口被销毁（如 explorer 重建桌面）时，重新创建一个。
        private void RecreateOverlay()
        {
            OverlayWindow old = _overlay;
            old.Died -= RecreateOverlay;
            try { old.Close(); }
            catch { }
            _overlay = new OverlayWindow(_cfg);
            _overlay.Died += RecreateOverlay;
            _overlay.Show();
            _overlay.Start();
            _overlay.SetDesktopVisible(_overlayVisible);
        }

        /// <summary>
        /// 测试下课提示：弹出一次示例通知（停留 3 秒后滑走，无声音），约 5 秒后自动退出。
        /// lastClass=true 时演示“今天最后一节课”的提示（含“今日课程已上完，辛苦啦喵~”）。
        /// </summary>
        public void StartTestToast(bool lastClass)
        {
            _cfg = ConfigStore.Load();
            _toast = new ToastWindow(_cfg);
            if (lastClass)
            {
                _toast.ShowToast(
                    "下课提醒",
                    "自习（晚自习二 19:55 - 20:40）已结束",
                    "今日课程已上完，辛苦啦喵~");
            }
            else
            {
                _toast.ShowToast(
                    "下课提醒",
                    "语文（第二节 08:55 - 09:40）已结束",
                    "下一节：数学  10:00 开始");
            }
            DispatcherTimer t = new DispatcherTimer();
            t.Interval = TimeSpan.FromSeconds(5);
            t.Tick += delegate
            {
                t.Stop();
                Application.Current.Shutdown();
            };
            t.Start();
        }

        private void OpenSettings()
        {
            _settings.Open(_cfg);
        }

        /// 按配置显示/隐藏右侧书签。
        private void ApplyBookmark()
        {
            if (_bookmark == null) return;
            bool show = _cfg.ShowBookmark == true;
            if (show) _bookmark.Show();
            else _bookmark.Hide();
        }

        private void HideBookmark()
        {
            _cfg.ShowBookmark = false;
            try { ConfigStore.Save(_cfg); }
            catch { }
            ApplyBookmark();
            Notice.Show(
                "已隐藏右侧的「设置」书签。\n\n需要重新显示时，请在右下角托盘图标 → 设置 里\n勾选「在屏幕右侧显示设置书签」即可。",
                MessageBoxImage.Information);
        }

        private void ReloadConfig()
        {
            _cfg = ConfigStore.Load();
            WarnConfigError();
            WarnMigration();
            _overlay.UpdateConfig(_cfg);
            _toast.UpdateConfig(_cfg);
            _reminder.UpdateConfig(_cfg);
            _overlay.ApplyTextMode();
            ApplyBookmark();
            UpdateTrayStatus();
        }

        private void ToggleOverlay()
        {
            _overlayVisible = !_overlayVisible;
            _overlay.SetDesktopVisible(_overlayVisible);
            _tray.SetOverlayVisible(_overlayVisible);
        }

        private void OnSettingsSaved(AppConfig cfg)
        {
            _cfg = cfg;
            _overlay.UpdateConfig(_cfg);
            _toast.UpdateConfig(_cfg);
            _reminder.UpdateConfig(_cfg);
            _overlay.ApplyTextMode();
            ApplyBookmark();
            UpdateTrayStatus();
            WarnSaveNotice();          // 如“程序目录不可写、已存到用户目录”等提示
        }

        /// 托盘悬停提示：实时显示运行状态（每分钟刷新）。
        private void UpdateTrayStatus()
        {
            if (_tray == null) return;
            StringBuilder sb = new StringBuilder();
            sb.Append("课堂助手 · 运行中");
            if (_cfg != null && _cfg.Events != null)
            {
                foreach (EventItem ev in _cfg.Events)
                {
                    if (ev == null || !ev.Enabled) continue;
                    DateTime d;
                    if (!ev.TryGetDate(out d)) continue;
                    int days = (d.Date - DateTime.Today).Days;
                    string name = (ev.Name == null || ev.Name.Trim().Length == 0) ? "倒计时" : ev.Name.Trim();
                    sb.Append("｜");
                    if (days > 0) sb.AppendFormat("{0} {1} 天", name, days);
                    else if (days == 0) sb.AppendFormat("{0} 今天", name);
                    else sb.AppendFormat("{0} 已过 {1} 天", name, -days);
                    break;
                }
            }
            _tray.SetStatus(sb.ToString());
        }

        private void StartStatusTimer()
        {
            if (_statusTimer != null) return;
            _statusTimer = new DispatcherTimer();
            _statusTimer.Interval = TimeSpan.FromSeconds(60);
            _statusTimer.Tick += delegate { UpdateTrayStatus(); };
            _statusTimer.Start();
        }

        private void OnReminderFire(string title, string line1, string line2)
        {
            _toast.ShowToast(title, line1, line2);
        }

        private void Exit()
        {
            _reminder.Stop();
            _tray.Dispose();
            _settings.ForceClose();
            _bookmark.Close();
            _toast.Close();
            _overlay.Close();
            Application.Current.Shutdown();
        }
    }

    // =================================================================
    //  入口
    // =================================================================

    public static class Program
    {
        /// 自检日志同时写到程序目录和临时目录，便于排查。
        public static void WriteSelfTestLog(string text)
        {
            string content = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "\r\n" + text;
            string[] paths = new string[]
            {
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "selftest.log"),
                Path.Combine(Path.GetTempPath(), "classroom-assistant-selftest.log")
            };
            foreach (string p in paths)
            {
                try { File.WriteAllText(p, content, Encoding.UTF8); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine("selftest log write failed: " + ex.Message); }
            }
        }

        [STAThread]
        public static void Main(string[] args)
        {
            bool selfTest = args != null && args.Length > 0 &&
                            string.Equals(args[0], "--selftest", StringComparison.OrdinalIgnoreCase);
            bool testToastLast = args != null && args.Length > 0 &&
                             string.Equals(args[0], "--testtoast-last", StringComparison.OrdinalIgnoreCase);
            bool testToast = testToastLast || (args != null && args.Length > 0 &&
                             string.Equals(args[0], "--testtoast", StringComparison.OrdinalIgnoreCase));
            if (selfTest) WriteSelfTestLog("SELFTEST STARTING, args=" + (args == null ? 0 : args.Length));

            // 单实例保护（自检、测试下课提示这两种模式除外）
            Mutex mutex = null;
            if (!selfTest && !testToast)
            {
                bool createdNew;
                mutex = new Mutex(true, "ClassroomAssistant_SingleInstance", out createdNew);
                if (!createdNew)
                {
                    Notice.Show(
                        "课堂助手已经在运行中。\n\n打开设置：双击右下角托盘图标，或点击屏幕右缘的「设置」书签。",
                        MessageBoxImage.Information);
                    return;
                }
            }

            try
            {
                Application app = new Application();
                app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                Shell shell = new Shell();
                app.Startup += delegate(object sender, StartupEventArgs e)
                {
                    if (testToast)
                    {
                        shell.StartTestToast(testToastLast);
                        return;
                    }
                    shell.Start(selfTest);
                    if (selfTest) app.Shutdown(0);
                };
                app.Run();
            }
            catch (Exception ex)
            {
                if (selfTest)
                {
                    WriteSelfTestLog("SELFTEST FAILED\r\n" + ex);
                    Environment.ExitCode = 1;
                }
                else
                {
                    Notice.Show("课堂助手启动失败：" + ex.Message, MessageBoxImage.Error);
                }
            }

            GC.KeepAlive(mutex);
        }
    }
}
