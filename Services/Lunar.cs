using System;
using System.Globalization;

namespace DesktopBar.Services
{
    /// <summary>农历(含闰月、天干地支、生肖)。</summary>
    public static class Lunar
    {
        private static readonly ChineseLunisolarCalendar C = new ChineseLunisolarCalendar();
        private static readonly string[] Stems = { "甲", "乙", "丙", "丁", "戊", "己", "庚", "辛", "壬", "癸" };
        private static readonly string[] Branches = { "子", "丑", "寅", "卯", "辰", "巳", "午", "未", "申", "酉", "戌", "亥" };
        private static readonly string[] Zodiac = { "鼠", "牛", "虎", "兔", "龙", "蛇", "马", "羊", "猴", "鸡", "狗", "猪" };
        private static readonly string[] MonthNames = { "正", "二", "三", "四", "五", "六", "七", "八", "九", "十", "冬", "腊" };
        private static readonly string[] Pre = { "初", "十", "廿", "三" };
        private static readonly string[] Nums = { "一", "二", "三", "四", "五", "六", "七", "八", "九", "十" };

        public static string ToLunarString(DateTime date)
        {
            try
            {
                int y = C.GetYear(date);
                int m = C.GetMonth(date);
                int d = C.GetDayOfMonth(date);
                int leap = C.GetLeapMonth(y);
                bool isLeap = false;
                int realMonth = m;
                if (leap > 0)
                {
                    if (m == leap) { isLeap = true; realMonth = m - 1; }
                    else if (m > leap) realMonth = m - 1;
                }
                string year = Stems[(y - 4) % 10] + Branches[(y - 4) % 12] + Zodiac[(y - 4) % 12] + "年";
                string month = (isLeap ? "闰" : "") + MonthNames[realMonth - 1] + "月";
                return year + " " + month + DayName(d);
            }
            catch { return ""; }
        }

        private static string DayName(int d)
        {
            if (d == 10) return "初十";
            if (d == 20) return "二十";
            if (d == 30) return "三十";
            return Pre[(d - 1) / 10] + Nums[(d - 1) % 10];
        }
    }
}
