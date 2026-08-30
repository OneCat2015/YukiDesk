using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace DesktopBar.Services
{
    /// <summary>
    /// 节日与节气:农历节日用系统农历(ChineseLunisolarCalendar,覆盖 1901-2100)实时推算,
    /// 节气用太阳视黄经天文算法(Meeus)实时计算,精度到分钟级,无需内嵌任何年份表。
    /// </summary>
    public static class Festivals
    {
        private static readonly ChineseLunisolarCalendar C = new ChineseLunisolarCalendar();

        // 按公历月份顺序排列的 24 节气:小寒(约1/5) ... 冬至(约12/22)。
        // 节气是太阳视黄经每 15° 的时刻:小寒=285°,春分=0°,夏至=90°,冬至=270°。
        private static readonly string[] TermNames =
        {
            "小寒", "大寒", "立春", "雨水", "惊蛰", "春分", "清明", "谷雨",
            "立夏", "小满", "芒种", "夏至", "小暑", "大暑", "立秋", "处暑",
            "白露", "秋分", "寒露", "霜降", "立冬", "小雪", "大雪", "冬至"
        };

        private static readonly (int m, int d, string name)[] SolarFestivals =
        {
            (1, 1, "元旦"), (2, 14, "情人节"), (3, 8, "妇女节"), (3, 12, "植树节"),
            (4, 1, "愚人节"), (5, 1, "劳动节"), (5, 4, "青年节"), (6, 1, "儿童节"),
            (7, 1, "建党节"), (8, 1, "建军节"), (9, 10, "教师节"), (10, 1, "国庆节"),
            (12, 25, "圣诞节")
        };

        private static readonly Dictionary<(int year, int idx), DateTime> TermCache = new Dictionary<(int year, int idx), DateTime>();
        private static int _labelDay = -1;
        private static string _label = "";

        static Festivals()
        {
            // 设置 YUKI_DEBUG_FEST=1 时输出 2026 年全部节气与主要农历节日,用于人工核对。
            if (Environment.GetEnvironmentVariable("YUKI_DEBUG_FEST") != "1") return;
            try
            {
                var sb = new StringBuilder();
                for (int i = 0; i < 24; i++)
                    sb.Append(TermNames[i]).Append('=').Append(SolarTerm(2026, i).ToString("M-d")).Append(' ');
                App.Log("节气自检 2026: " + sb);
                var spring = new DateTime(2026, 2, 17); // 2026 年春节(正月初一)
                sb = new StringBuilder();
                for (int offset = 0; offset < 8; offset++)
                {
                    string f = LunarFestival(spring.AddDays(offset));
                    if (f.Length > 0) sb.Append(spring.AddDays(offset).ToString("M-d")).Append('=').Append(f).Append(' ');
                }
                App.Log("农历节日自检 2026 春节后一周: " + sb);
            }
            catch (Exception ex) { App.Log("节日自检失败: " + ex.Message); }
        }

        /// <summary>返回指定日期命中的节日/节气,多个用 " · " 连接;没有则返回空串。</summary>
        public static string GetTodayLabel(DateTime date)
        {
            int key = date.Year * 10000 + date.Month * 100 + date.Day;
            if (_labelDay == key) return _label;
            var items = new List<string>();
            string f = LunarFestival(date);
            if (f.Length > 0) items.Add(f);
            f = SolarFestival(date);
            if (f.Length > 0) items.Add(f);
            f = SolarTermName(date);
            if (f.Length > 0) items.Add(f);
            _labelDay = key;
            _label = string.Join(" · ", items);
            return _label;
        }

        private static string SolarFestival(DateTime date)
        {
            foreach (var (m, d, name) in SolarFestivals)
                if (date.Month == m && date.Day == d) return name;
            return "";
        }

        private static string LunarFestival(DateTime date)
        {
            try
            {
                int y = C.GetYear(date);
                int m = C.GetMonth(date);
                int d = C.GetDayOfMonth(date);
                int leap = C.GetLeapMonth(y);
                bool isLeap = leap > 0 && m == leap;
                int realMonth = m;
                if (leap > 0 && m > leap) realMonth = m - 1;
                if (!isLeap)
                {
                    switch (realMonth * 100 + d)
                    {
                        case 101: return "春节";
                        case 115: return "元宵节";
                        case 202: return "龙抬头";
                        case 303: return "上巳节";
                        case 505: return "端午节";
                        case 707: return "七夕节";
                        case 715: return "中元节";
                        case 815: return "中秋节";
                        case 909: return "重阳节";
                        case 1208: return "腊八节";
                        case 1223: return "小年";
                    }
                }
                // 除夕:明天是下一年的正月初一(闰月、大小月自动正确)。
                DateTime next = date.AddDays(1);
                int ny = C.GetYear(next);
                int nm = C.GetMonth(next);
                int nd = C.GetDayOfMonth(next);
                int nleap = C.GetLeapMonth(ny);
                bool nLeap = nleap > 0 && nm == nleap;
                int nReal = nm;
                if (nleap > 0 && nm > nleap) nReal = nm - 1;
                if (!nLeap && nReal == 1 && nd == 1) return "除夕";
                return "";
            }
            catch { return ""; }
        }

        private static string SolarTermName(DateTime date)
        {
            // 该公历年的 24 个节气全部落在本公历年内,直接逐个比对日期。
            for (int i = 0; i < 24; i++)
                if (SolarTerm(date.Year, i) == date.Date) return TermNames[i];
            return "";
        }

        /// <summary>计算某年第 idx 个节气(0=小寒 ... 23=冬至)落下的北京时间日期。</summary>
        public static DateTime SolarTerm(int year, int idx)
        {
            var key = (year, idx);
            if (TermCache.TryGetValue(key, out var hit)) return hit;

            double targetLon = (285.0 + 15.0 * idx) % 360.0;
            double jde = ToJde(new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            double lon0 = SunLongitude(jde);
            jde += (targetLon - lon0 + 360.0) % 360.0 / 0.98565; // 太阳平均每日约 0.98565°
            for (int i = 0; i < 8; i++)
            {
                double diff = ((targetLon - SunLongitude(jde) + 540.0) % 360.0) - 180.0;
                jde += diff / 0.98565;
                if (Math.Abs(diff) < 0.00002) break; // <2 秒,日期已定
            }
            // JDE 为 TT 时标;ΔT(≈1 分钟)不影响日期。节气时刻按北京时间(UTC+8)归属日期。
            DateTime beijing = JdeToUtc(jde).AddHours(8);
            var result = beijing.Date;
            TermCache[key] = result;
            return result;
        }

        private static double SunLongitude(double jde)
        {
            double t = (jde - 2451545.0) / 36525.0;
            double l0 = 280.46646 + 36000.76983 * t + 0.0003032 * t * t;
            double m = 357.52911 + 35999.05029 * t - 0.0001537 * t * t;
            double mr = m * Math.PI / 180.0;
            double c = (1.914602 - 0.004817 * t - 0.000014 * t * t) * Math.Sin(mr)
                     + (0.019993 - 0.000101 * t) * Math.Sin(2 * mr)
                     + 0.000289 * Math.Sin(3 * mr);
            double lon = (l0 + c) % 360.0;
            return lon < 0 ? lon + 360.0 : lon;
        }

        private static double ToJde(DateTime utc)
            => utc.Subtract(new DateTime(2000, 1, 1, 12, 0, 0, DateTimeKind.Utc)).TotalDays + 2451545.0;

        private static DateTime JdeToUtc(double jde)
            => new DateTime(2000, 1, 1, 12, 0, 0, DateTimeKind.Utc).AddDays(jde - 2451545.0);
    }
}
