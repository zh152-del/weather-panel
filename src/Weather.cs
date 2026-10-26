using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace WeatherPanel;

/// <summary>Open-Meteo 数据获取与解析。字段与坐标沿用原 Python 版，不换数据源。</summary>
internal static class Weather
{
    public const double Lat = 22.8400, Lon = 108.3000;
    public const string CityName = "南宁 · 西乡塘";
    public const string CnCityCode = "101300101";      // 中国天气网：南宁

    private static readonly HttpClient Http = CreateHttp();

    private static HttpClient CreateHttp()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) WeatherPanel/2.0");
        // 中国天气网的 d1 接口不带 Referer 会返回错误页
        c.DefaultRequestHeaders.Referrer = new Uri("http://www.weather.com.cn/");
        return c;
    }

    private static readonly string[] Dirs = { "北", "东北", "东", "东南", "南", "西南", "西", "西北" };
    private static readonly string[] Week = { "周一", "周二", "周三", "周四", "周五", "周六", "周日" };

    private static readonly Dictionary<int, string> Wmo = new()
    {
        [0] = "晴", [1] = "晴间多云", [2] = "多云", [3] = "阴", [45] = "雾", [48] = "雾凇",
        [51] = "毛毛雨", [53] = "毛毛雨", [55] = "毛毛雨", [56] = "冻毛毛雨", [57] = "冻毛毛雨",
        [61] = "小雨", [63] = "中雨", [65] = "大雨", [66] = "冻雨", [67] = "冻雨",
        [71] = "小雪", [73] = "中雪", [75] = "大雪", [77] = "米雪",
        [80] = "阵雨", [81] = "阵雨", [82] = "强阵雨", [85] = "阵雪", [86] = "阵雪",
        [95] = "雷暴", [96] = "雷暴伴冰雹", [99] = "雷暴伴冰雹",
    };

    // 单色符号（Segoe UI Symbol），与金属材质统一，不用彩色 emoji
    private static readonly Dictionary<int, string> Icons = new()
    {
        [0] = "\u2600", [1] = "\u26c5", [2] = "\u2601", [3] = "\u2601", [45] = "\u2601", [48] = "\u2601",
        [51] = "\u2602", [53] = "\u2602", [55] = "\u2602", [56] = "\u2602", [57] = "\u2602",
        [61] = "\u2602", [63] = "\u2602", [65] = "\u2602", [66] = "\u2602", [67] = "\u2602",
        [71] = "\u2744", [73] = "\u2744", [75] = "\u2744", [77] = "\u2744",
        [80] = "\u2602", [81] = "\u2602", [82] = "\u2602", [85] = "\u2744", [86] = "\u2744",
        [95] = "\u26a1", [96] = "\u26a1", [99] = "\u26a1",
    };

    public sealed class Day
    {
        public string Date = "";
        public int Code;
        public int TMax, TMin, Pop;
        public string Text = "";        // 中文天气（中国天气网提供）
    }

    public sealed class Hour
    {
        public int HourOfDay, Temp, Code, Pop;
    }

    public sealed class Data
    {
        public int Temp, Feels, Hum, Wind, WindDir, Gust, Cloud, Pressure, Dew, Code;
        public bool IsDay;
        public string Desc = "";        // 中文天气描述（优先中国天气网）
        public string WindText = "";    // 「东北 4」这类现成风力文字（中国天气网）
        public string Source = "";      // 数据来源，写日志用
        public List<string> CnDayTexts = new();   // 中国天气网 7 天中文天气（页面 HTML）
        public double Precip, Visibility, Uv;
        public string Sunrise = "", Sunset = "", Fetched = "";
        public List<Day> Days = new();
        public List<Hour> Hours = new();
    }

    /// <summary>
    /// 数据来源：主数据取中国天气网（实况 + 5 天预报的温度），
    /// 中国天气网没有的字段（体感/露点/阵风/UV/云量/降水概率/逐小时/第 6 天）
    /// 由 Open-Meteo 补齐。任一侧失败都不影响面板显示。
    /// </summary>
    public static async Task<Data?> FetchAsync()
    {
        var omTask = FetchOpenMeteoAsync();
        var cnTask = FetchChinaAsync();
        var om = await omTask.ConfigureAwait(false);
        var cn = await cnTask.ConfigureAwait(false);

        if (om == null && cn == null) return null;
        if (om == null) { cn!.Source = "china-weather"; Log("source=china-weather only"); return cn; }
        if (cn == null) { om.Source = "open-meteo"; Log("source=open-meteo only (CN failed)"); return om; }

        // 主字段用中国天气网实况
        om.Temp = cn.Temp;
        om.Desc = cn.Desc;
        om.Hum = cn.Hum;
        om.Pressure = cn.Pressure;
        om.Visibility = cn.Visibility;
        om.Precip = cn.Precip;
        om.Wind = cn.Wind;
        om.WindText = cn.WindText;
        om.IsDay = cn.IsDay;

        // 5 天预报的温度用中国天气网，第 6 天沿用 Open-Meteo；图标与文字仍按 WMO code
        int n = Math.Min(cn.Days.Count, om.Days.Count);
        for (int i = 0; i < n; i++)
        {
            om.Days[i].TMax = cn.Days[i].TMax;
            om.Days[i].TMin = cn.Days[i].TMin;
        }
        // 天气文字：中国天气网 7 天页面覆盖前 6 天（图标随文字走，避免与实况"晴"打架）
        for (int i = 0; i < cn.CnDayTexts.Count && i < om.Days.Count; i++)
            om.Days[i].Text = cn.CnDayTexts[i];
        om.Source = "china-weather+open-meteo(5d temps)";
        return om;
    }

    // ===== 中国天气网（d1.weather.com.cn，需 Referer）=====
    private static async Task<Data?> FetchChinaAsync()
    {
        try
        {
            string ts = DateTimeOffset.Now.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
            var skTask = Http.GetStringAsync(
                "http://d1.weather.com.cn/sk_2d/" + CnCityCode + ".html?_=" + ts);
            var idxTask = Http.GetStringAsync(
                "http://d1.weather.com.cn/weather_index/" + CnCityCode + ".html?_=" + ts);
            // 7 天页面才有中文天气文字（JSON 预报只给温度）
            var htmlTask = Http.GetStringAsync(
                "http://www.weather.com.cn/weather/" + CnCityCode + ".shtml");

            string sk = await skTask.ConfigureAwait(false);
            var skJson = ExtractJson(sk, "dataSK");
            if (skJson == null) { Log("CN sk parse fail"); return null; }
            var e = skJson.Value;

            var d = new Data
            {
                Temp = (int)Math.Round(Dbl(e, "temp")),
                Hum = (int)Math.Round(Pct(Str(e, "SD"))),
                Pressure = (int)Math.Round(Dbl(e, "qy")),
                Visibility = Km(Str(e, "njd")),
                Precip = Math.Round(Dbl(e, "rain"), 1),
                Desc = Str(e, "weather"),
                Wind = (int)Math.Round(Kmh(Str(e, "wse"))),
                IsDay = !Str(e, "weathercode").StartsWith("n", StringComparison.OrdinalIgnoreCase),
                Days = new List<Day>(),
                Hours = new List<Hour>(),
            };
            int kmh = d.Wind;
            string wd = Str(e, "WD").Replace("风", "");
            if (!string.IsNullOrEmpty(wd)) d.WindText = wd + " " + kmh;

            try
            {
                string html = await htmlTask.ConfigureAwait(false);
                foreach (Match m in Regex.Matches(html, @"<p[^>]*class=""wea""[^>]*>([^<]*)</p>"))
                {
                    var wt = m.Groups[1].Value.Trim();
                    if (wt.Length > 0) d.CnDayTexts.Add(wt);
                }
            }
            catch (Exception ex) { Log("CN html fail " + ex.Message); }

            string idx = await idxTask.ConfigureAwait(false);
            var fc = ExtractJson(idx, "fc");
            if (fc != null && fc.Value.TryGetProperty("f", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var it in arr.EnumerateArray())
                {
                    d.Days.Add(new Day
                    {
                        Date = Str(it, "fi"),
                        TMax = (int)Math.Round(Dbl(it, "fc")),
                        TMin = (int)Math.Round(Dbl(it, "fd")),
                        Pop = 0,
                    });
                }
            }
            Log("CN ok temp=" + d.Temp + " desc=" + d.Desc + " days=" + d.Days.Count);
            return d.Days.Count == 0 ? null : d;
        }
        catch (Exception ex)
        {
            Log("CN fetch fail " + ex.GetType().Name + ": " + ex.Message);
            return null;
        }
    }

    /// <summary>从 "var name ={...};" 的 JS 文本里抠出第一段完整 JSON。</summary>
    private static JsonElement? ExtractJson(string text, string varName)
    {
        int i = text.IndexOf("var " + varName, StringComparison.Ordinal);
        if (i < 0) return null;
        int s = text.IndexOf('{', i);
        if (s < 0) return null;
        int depth = 0;
        bool inStr = false;
        for (int k = s; k < text.Length; k++)
        {
            char c = text[k];
            if (c == '"' && (k == 0 || text[k - 1] != '\\')) inStr = !inStr;
            if (inStr) continue;
            if (c == '{') depth++;
            else if (c == '}')
            {
                depth--;
                if (depth == 0)
                {
                    try { return JsonDocument.Parse(text.Substring(s, k - s + 1)).RootElement.Clone(); }
                    catch (Exception ex) { Log("CN json parse " + varName + " " + ex.Message); return null; }
                }
            }
        }
        return null;
    }

    private static string Str(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return "";
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString() ?? "",
            JsonValueKind.Number => v.ToString(),
            _ => "",
        };
    }

    private static double Dbl(JsonElement e, string name)
    {
        var s = Str(e, name);
        return double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : 0;
    }

    /// <summary>"59%" → 59</summary>
    private static double Pct(string s) =>
        double.TryParse(s.TrimEnd('%'), NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : 0;

    /// <summary>"22km" → 22000（与 Open-Meteo 的 visibility 单位一致：米）</summary>
    private static double Km(string s)
    {
        var t = s.ToLowerInvariant().Replace("km", "").Replace("公里", "").Trim();
        return double.TryParse(t, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d * 1000 : 0;
    }

    /// <summary>"4km/h" → 4</summary>
    private static double Kmh(string s)
    {
        var t = s.ToLowerInvariant().Replace("km/h", "").Replace("kmh", "").Trim();
        return double.TryParse(t, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : 0;
    }

    /// <summary>中文天气文字 → 图标文件名（中国天气网实况用，避免猜 weathercode 编码）。</summary>
    public static string IconFileByText(string text, bool isDay = true)
    {
        if (string.IsNullOrWhiteSpace(text)) return isDay ? "sunny" : "night";
        if (text.Contains("雷")) return "thunder_rain";
        if (text.Contains("冰雹")) return "hail";
        if (text.Contains("雨夹雪") || text.Contains("冻雨")) return "sleet";
        if (text.Contains("雪")) return "snow";
        if (text.Contains("雾")) return "fog";
        if (text.Contains("霾")) return "haze";
        if (text.Contains("沙") || text.Contains("尘")) return "dust";
        if (text.Contains("暴雨") || text.Contains("大雨")) return "heavy_rain";
        if (text.Contains("中雨")) return "rain";
        if (text.Contains("阵雨")) return "shower";
        if (text.Contains("雨")) return "drizzle";
        if (text.Contains("阴")) return "overcast";
        if (text.Contains("云")) return "cloudy";
        if (text.Contains("晴")) return isDay ? "sunny" : "night";
        if (text.Contains("风")) return "wind";
        return isDay ? "sunny" : "night";
    }

    /// <summary>WMO code → Assets/weather 下的图标文件名（昼夜区分）。</summary>
    public static string IconFile(int code, bool isDay = true)
    {
        switch (code)
        {
            case 0: return isDay ? "sunny" : "night";
            case 1: return isDay ? "partly" : "night";
            case 2: return "cloudy";
            case 3: return "overcast";
            case 45: return "fog";
            case 48: return "frost";
            case 51: case 53: case 55: return "drizzle";
            case 56: case 57: case 66: case 67: return "sleet";
            case 61: return "drizzle";
            case 63: return "rain";
            case 65: return "heavy_rain";
            case 71: case 73: case 75: case 77: return "snow";
            case 80: case 81: return "shower";
            case 82: return "heavy_rain";
            case 85: case 86: return "snow";
            case 95: return "thunder_rain";
            case 96: case 99: return "hail";
            default: return "cloudy";
        }
    }

    public static string Desc(int code) => Wmo.TryGetValue(code, out var s) ? s : "未知";
    public static string Icon(int code) => Icons.TryGetValue(code, out var s) ? s : "·";
    public static string WindDir(int deg) => Dirs[(int)Math.Round(deg / 45.0) % 8];
    public static string Weekday(DateTime d) => Week[(int)d.DayOfWeek == 0 ? 6 : (int)d.DayOfWeek - 1];

    public static string MoonPhase(DateTime date)
    {
        double age = ((date.Date - new DateTime(2000, 1, 6)).TotalDays % 29.53 + 29.53) % 29.53;
        if (age < 1) return "新月";
        if (age < 7.4) return "上弦月";
        if (age < 14.8) return "盈凸月";
        if (age < 16.2) return "满月";
        if (age < 22.1) return "亏凸月";
        if (age < 25.5) return "下弦月";
        return "残月";
    }

    private static async Task<Data?> FetchOpenMeteoAsync()
    {
        try
        {
            string url =
                "https://api.open-meteo.com/v1/forecast?latitude=" + Lat.ToString("F4", CultureInfo.InvariantCulture) +
                "&longitude=" + Lon.ToString("F4", CultureInfo.InvariantCulture) +
                "&current=temperature_2m,relative_humidity_2m,apparent_temperature," +
                "weather_code,wind_speed_10m,wind_direction_10m,wind_gusts_10m," +
                "precipitation,cloud_cover,surface_pressure,is_day,visibility,dew_point_2m" +
                "&hourly=temperature_2m,weather_code,precipitation_probability" +
                "&daily=weather_code,temperature_2m_max,temperature_2m_min," +
                "precipitation_probability_max,wind_speed_10m_max,uv_index_max,sunrise,sunset" +
                "&timezone=auto&forecast_days=10";

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.UserAgent.ParseAdd("Mozilla/5.0 WeatherPanel/2.0");
            using var resp = await Http.SendAsync(req).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync().ConfigureAwait(false));
            var root = doc.RootElement;
            var cur = root.GetProperty("current");
            var daily = root.GetProperty("daily");
            var hourly = root.GetProperty("hourly");

            var d = new Data
            {
                Temp = Round(cur, "temperature_2m"),
                Feels = Round(cur, "apparent_temperature"),
                Hum = Round(cur, "relative_humidity_2m"),
                Wind = Round(cur, "wind_speed_10m"),
                WindDir = Round(cur, "wind_direction_10m"),
                Gust = Round(cur, "wind_gusts_10m"),
                Cloud = Round(cur, "cloud_cover"),
                Pressure = Round(cur, "surface_pressure"),
                Dew = Round(cur, "dew_point_2m"),
                Code = Round(cur, "weather_code"),
                IsDay = Num(cur, "is_day") >= 1,
                Precip = Math.Round(Num(cur, "precipitation"), 1),   // 当前降水量 mm，不是当日概率
                Visibility = Num(cur, "visibility"),
                Uv = NumArr(daily, "uv_index_max", 0),
                Sunrise = TimePart(daily, "sunrise", 0),
                Sunset = TimePart(daily, "sunset", 0),
                Fetched = DateTime.Now.ToString("HH:mm"),
            };

            var dates = daily.GetProperty("time");
            var codes = daily.GetProperty("weather_code");
            var tmax = daily.GetProperty("temperature_2m_max");
            var tmin = daily.GetProperty("temperature_2m_min");
            var pops = daily.TryGetProperty("precipitation_probability_max", out var pp) ? pp : default;
            for (int i = 0; i < dates.GetArrayLength(); i++)
            {
                d.Days.Add(new Day
                {
                    Date = dates[i].GetString() ?? "",
                    Code = codes[i].GetInt32(),
                    TMax = (int)Math.Round(tmax[i].GetDouble()),
                    TMin = (int)Math.Round(tmin[i].GetDouble()),
                    Pop = pops.ValueKind == JsonValueKind.Array && pops[i].ValueKind == JsonValueKind.Number
                        ? (int)Math.Round(pops[i].GetDouble()) : 0,
                });
            }

            var hTimes = hourly.GetProperty("time");
            var hTemps = hourly.GetProperty("temperature_2m");
            var hCodes = hourly.GetProperty("weather_code");
            var hPops = hourly.TryGetProperty("precipitation_probability", out var hp) ? hp : default;
            var now = DateTime.Now;
            for (int i = 0; i < hTimes.GetArrayLength() && d.Hours.Count < 8; i++)
            {
                if (!DateTime.TryParse(hTimes[i].GetString(), CultureInfo.InvariantCulture,
                                       DateTimeStyles.None, out var t)) continue;
                if (t < now.AddHours(-1)) continue;
                d.Hours.Add(new Hour
                {
                    HourOfDay = t.Hour,
                    Temp = (int)Math.Round(hTemps[i].GetDouble()),
                    Code = hCodes[i].GetInt32(),
                    Pop = hPops.ValueKind == JsonValueKind.Array && hPops[i].ValueKind == JsonValueKind.Number
                        ? (int)Math.Round(hPops[i].GetDouble()) : 0,
                });
            }

            return d.Days.Count == 0 ? null : d;
        }
        catch (Exception ex)
        {
            Log("fetch fail " + ex.GetType().Name + ": " + ex.Message);
            return null;
        }
    }

    // 日志写到用户目录而不是程序目录：装到 Program Files 时只读，卸载也不会残留文件
    private static readonly string LogPath = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WeatherPanel", "Logs", "fetch.log");

    public static void Log(string msg)
    {
        try
        {
            // 长时间断网会反复写失败日志，超过 256KB 就截断，避免文件一直长
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(LogPath)!);
            var fi = new System.IO.FileInfo(LogPath);
            if (fi.Exists && fi.Length > 256 * 1024)
                System.IO.File.WriteAllText(LogPath, "");
            System.IO.File.AppendAllText(LogPath,
                DateTime.Now.ToString("[MM-dd HH:mm:ss] ") + msg + Environment.NewLine);
        }
        catch { }
    }

    private static double Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;

    private static int Round(JsonElement e, string name) => (int)Math.Round(Num(e, name));

    private static double NumArr(JsonElement e, string name, int idx) =>
        e.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array &&
        a.GetArrayLength() > idx && a[idx].ValueKind == JsonValueKind.Number
            ? a[idx].GetDouble() : 0;

    private static string TimePart(JsonElement e, string name, int idx)
    {
        if (!e.TryGetProperty(name, out var a) || a.ValueKind != JsonValueKind.Array ||
            a.GetArrayLength() <= idx) return "";
        var s = a[idx].GetString() ?? "";
        int k = s.IndexOf('T');
        return k >= 0 ? s[(k + 1)..] : s;
    }
}
