using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Net.NetworkInformation;
using System.Windows.Threading;

namespace WeatherPanel;

public sealed class HourCell
{
    public string Time { get; set; } = "";
    public ImageSource? IconSource { get; set; }
    public string Temp { get; set; } = "";
    public string Pop { get; set; } = "";
    public Brush TimeBrush { get; set; } = Brushes.White;
}

public sealed class DayRow
{
    public string Name { get; set; } = "";
    public ImageSource? IconSource { get; set; }
    public string Desc { get; set; } = "";
    public string TMin { get; set; } = "";
    public string TMax { get; set; } = "";
    public string Pop { get; set; } = "";
    public Brush NameBrush { get; set; } = Brushes.White;
    public Brush BarBrush { get; set; } = Brushes.Gray;
    public Brush DotBrush { get; set; } = Brushes.White;
    public Thickness BarMargin { get; set; }
    public double BarWidth { get; set; } = 3;
    public Thickness DotMargin { get; set; }
}

public partial class MainWindow : Window
{
    private const double BarTrack = 76;
    private const int DayCount = 6;

    private static readonly Brush CAcc = Freeze(0x7F, 0xD4, 0xFF);
    private static readonly Brush CSub = Freeze(0xA6, 0xB2, 0xC2);
    private static readonly Brush CDim = Freeze(0x8A, 0x96, 0xA6);
    private static readonly Brush CBar = Freeze(0xA8, 0xBD, 0xD2);
    private static readonly Brush CDot = Freeze(0xC2, 0xCE, 0xDA);
    private static readonly Brush CDotToday = Freeze(0xDF, 0xF3, 0xFF);

    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _weather = new();   // 间隔由 ScheduleWeather 动态决定
    private string _mode = "normal";

    // 开机重试策略（刚开机常常还没连上网，固定 30 分钟一次等于空一屏）：
    //   阶段1：每 5 秒一次，共 30 次（约 2.5 分钟）
    //   阶段2：10 秒起、每轮 +5 秒，同样 30 次（10/15/.../155 秒）
    //   之后：保持 155 秒一直重试，直到拿到数据
    //   拿到数据后：转 30 分钟常规刷新
    private const int BootAttempts = 30;
    private const int SlowAttempts = 30;
    private static readonly TimeSpan BootInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan NormalInterval = TimeSpan.FromMinutes(30);
    // 运行中断网：固定 10 秒一次、连续 30 次（约 5 分钟）仍失败就弹「网络超时」覆盖层
    private const int RunRetryAttempts = 30;
    private static readonly TimeSpan RunRetryInterval = TimeSpan.FromSeconds(10);
    private int _bootAttempts;
    private int _slowAttempts;
    private int _runFailAttempts;
    private bool _everSucceeded;
    private bool _fetching;
    private bool _waitingForNetwork;      // 放弃轮询，改等系统网络事件
    private DateTime _lastNetEvent = DateTime.MinValue;
    private string _lastOkText = "";

    public MainWindow()
    {
        Log.W("MainWindowConstructorEntered");
        InitializeComponent();
        Log.W("MainWindowConstructorCompleted");

        SourceInitialized += (_, _) => Log.W("SourceInitialized", Snap("source-init"));
        ContentRendered += (_, _) => Log.W("ContentRendered", Snap("content-rendered"));
        Closing += (_, _) => Log.W("WindowClosing", Snap("closing"));
        Closed += (_, _) => Log.W("WindowClosed", Snap("closed"));
        IsVisibleChanged += (_, _) => Log.W("VisibilityChanged", Snap("vis-changed"));
    }

    private static readonly Dictionary<string, ImageSource> IconCache = new();

    /// <summary>加载打包的天气图标（Assets/weather/*.png），失败只记日志不影响其余界面。</summary>
    private static ImageSource? LoadIcon(string name)
    {
        if (IconCache.TryGetValue(name, out var cached)) return cached;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri("pack://application:,,,/Assets/weather/" + name + ".png");
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze();
            IconCache[name] = bmp;
            return bmp;
        }
        catch (Exception ex)
        {
            Log.Ex("IconLoadFailed:" + name, ex);
            return null;
        }
    }

    private static Brush Freeze(byte r, byte g, byte b)
    {
        var br = new SolidColorBrush(Color.FromRgb(r, g, b));
        br.Freeze();
        return br;
    }

    private static bool Has(string[] args, string flag) =>
        Array.Exists(args, a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));

    /// <summary>一次性抓取窗口真实状态，用于日志和事后比对。</summary>
    private string Snap(string tag)
    {
        IntPtr h = Native.Handle(this);
        return string.Format(
            "{0} hwnd=0x{1:X} rect={2} IsVisible={3} Visibility={4} WindowState={5} " +
            "Topmost={6} Left={7:0} Top={8:0} Size={9:0}x{10:0} ShowInTaskbar={11} " +
            "ex=0x{12:X} IsWindow={13} IsWindowVisible={14} Cloaked={15}",
            tag, h.ToInt64(), Native.Rect(h), IsVisible, Visibility, WindowState,
            Topmost, Left, Top, ActualWidth, ActualHeight, ShowInTaskbar,
            Native.ExStyle(h), Native.IsWin(h), Native.IsVis(h), Native.Cloaked(h));
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        Log.W("Loaded", Snap("loaded-at-entry"));

        var args = Environment.GetCommandLineArgs();
        bool diag = Has(args, "--diagnostic");
        _mode = diag ? "diagnostic" : Has(args, "--top") ? "topmost" : "normal";
        Log.W("ModeSelected", _mode);

        var wa = SystemParameters.WorkArea;
        Log.W("WorkArea", string.Format("({0},{1},{2},{3})", wa.Left, wa.Top, wa.Right, wa.Bottom));

        if (diag)
        {
            // 诊断模式：普通窗口，不碰 Z 序、不穿透，固定左上安全位，便于人工确认渲染
            ShowInTaskbar = true;
            Topmost = false;
            Left = wa.Left + 80;
            Top = wa.Top + 80;
        }
        else
        {
            Left = wa.Right - Width;
            Top = wa.Top;
            Native.ApplyPanelStyle(this, clickThrough: true);
            if (_mode == "topmost") Native.SetTopmost(this);
            else Native.RaiseInNormalLayer(this);
        }

        NoiseLayer.Fill = MetalMaterial.NoiseBrush;

        Tick(null, EventArgs.Empty);
        _clock.Tick += Tick;
        _clock.Start();
        _weather.Tick += (_, _) => _ = TickWeatherAsync();
        // 停止轮询后靠这两个系统事件唤醒，不再定时抓取（避免长期断网反复请求）
        NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
        NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
        ScheduleWeather(TimeSpan.FromSeconds(2));   // 开机 2 秒先抓一次，失败自动转 5 秒重试

        Log.W("WindowShown", Snap("shown"));
        Dispatcher.BeginInvoke(new Action(() =>
            Log.W("FirstVisibleFrameConfirmed", Snap("idle-frame"))), DispatcherPriority.ApplicationIdle);
    }

    private void Tick(object? sender, EventArgs e)
    {
        var now = DateTime.Now;
        ClockText.Text = now.ToString("HH:mm:ss");
        DateText.Text = now.Month + "月" + now.Day.ToString("00") + "日 " + Weather.Weekday(now);
    }

    /// <summary>
    /// 次数用完仍拿不到数据：停掉定时器、挂出提示，之后只在系统报告网络变化时才试一次。
    /// 这样长时间断网也不会持续发请求。
    /// </summary>
    private void StopAndWaitForNetwork(string reason)
    {
        _weather.Stop();
        _waitingForNetwork = true;
        ShowNetError(true);
        Log.W("WeatherWaitNetwork", reason + " -> 停止轮询，等系统网络事件");
    }

    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e)
    {
        if (!e.IsAvailable) return;                       // 只关心"网络恢复"
        Dispatcher.BeginInvoke(new Action(() => OnNetworkBack("AvailabilityChanged")));
    }

    private void OnNetworkAddressChanged(object? sender, EventArgs e)
    {
        // 连上 WiFi / 插网线 / 重新拨号都会触发；DHCP 拿到地址前会连发几次
        Dispatcher.BeginInvoke(new Action(() => OnNetworkBack("AddressChanged")));
    }

    private void OnNetworkBack(string source)
    {
        if ((DateTime.Now - _lastNetEvent).TotalSeconds < 3) return;   // 防抖，避免连发多次
        _lastNetEvent = DateTime.Now;
        Log.W("NetworkBack", source + " 检测到网络变化，试抓一次");
        _weather.Stop();
        _waitingForNetwork = false;
        _ = TickWeatherAsync();       // 成功则恢复 30 分钟刷新；失败会自动重新进入等待
    }

    /// <summary>断网覆盖层：整块盖住面板，抓到数据后立即隐藏，不常驻。</summary>
    private void ShowNetError(bool show)
    {
        bool currently = NetErrorLayer.Visibility == Visibility.Visible;
        if (show == currently) return;
        if (show)
        {
            NetErrorHint.Text = string.IsNullOrEmpty(_lastOkText)
                ? "尚未获取到数据"
                : "上次更新 " + _lastOkText;
            NetErrorLayer.Visibility = Visibility.Visible;
            Log.W("NetErrorShown", string.Format("显示超时提示（运行期失败 {0} 次，等待网络={1}）",
                _runFailAttempts, _waitingForNetwork));
        }
        else
        {
            NetErrorLayer.Visibility = Visibility.Collapsed;
            Log.W("NetErrorHidden", "已恢复，提示自动消失");
        }
    }

    private void ScheduleWeather(TimeSpan delay)
    {
        _weather.Stop();
        _weather.Interval = delay;
        _weather.Start();
    }

    /// <summary>一次抓取 + 决定下一次的间隔。开机阶段密集重试，成功后转常规间隔。</summary>
    private async System.Threading.Tasks.Task TickWeatherAsync()
    {
        if (_fetching) return;      // 上一轮还没回来（开机断网时请求会卡满超时），不叠加
        _fetching = true;
        bool ok;
        try
        {
            ok = await RefreshAsync();
        }
        finally
        {
            _fetching = false;
        }

        if (ok)
        {
            _everSucceeded = true;
            _bootAttempts = 0;
            _slowAttempts = 0;
            _runFailAttempts = 0;
            _waitingForNetwork = false;
            ShowNetError(false);        // 抓到数据就撤掉提示
            ScheduleWeather(NormalInterval);
            return;
        }

        if (!_everSucceeded)
        {
            if (_bootAttempts < BootAttempts)
            {
                // 阶段 1：密集快抓
                _bootAttempts++;
                ScheduleWeather(BootInterval);
                Log.W("WeatherBootRetry", string.Format("快抓 {0}/{1}，下次 {2}s",
                    _bootAttempts, BootAttempts, (int)BootInterval.TotalSeconds));
            }
            else if (_slowAttempts < SlowAttempts)
            {
                // 阶段 2：间隔逐轮 +5 秒（10/15/.../155）
                _slowAttempts++;
                var delay = TimeSpan.FromSeconds(5 + 5 * _slowAttempts);
                ScheduleWeather(delay);
                Log.W("WeatherBootRetry", string.Format("慢抓 {0}/{1}，下次 {2}s",
                    _slowAttempts, SlowAttempts, (int)delay.TotalSeconds));
            }
            else
            {
                // 两阶段都没拿到：停止轮询，改等系统网络事件（不再定时请求）
                StopAndWaitForNetwork("开机两阶段已用完");
            }
        }
        else
        {
            // 运行阶段断网：固定 10 秒一次，30 次（约 5 分钟）内不打扰用户
            _runFailAttempts++;
            if (_runFailAttempts > RunRetryAttempts)
            {
                StopAndWaitForNetwork("运行期已重试 " + RunRetryAttempts + " 次");
            }
            else
            {
                ScheduleWeather(RunRetryInterval);
                Log.W("WeatherRetry", string.Format("运行期失败 {0}/{1}，10s 后重试",
                    _runFailAttempts, RunRetryAttempts));
            }
        }
    }

    /// <summary>抓一次并写日志，返回是否拿到数据。</summary>
    private async System.Threading.Tasks.Task<bool> RefreshAsync()
    {
        var d = await Weather.FetchAsync();
        if (d == null)
        {
            Log.W("WeatherFetchFailed", "本轮无数据，保留上一屏");
            return false;
        }
        Apply(d);
        _lastOkText = DateTime.Now.ToString("HH:mm");
        Log.W("WeatherApplied", string.Format("{0}C {1} hours={2} days={3} source={4}",
            d.Temp, string.IsNullOrEmpty(d.Desc) ? Weather.Desc(d.Code) : d.Desc,
            d.Hours.Count, d.Days.Count, d.Source));
        return true;
    }

    private void Apply(Weather.Data d)
    {
        // 中国天气网有中文天气文字时优先用它（图标按文字匹配，避免猜天气码）
        bool hasCnText = !string.IsNullOrEmpty(d.Desc);
        CurIcon.Source = LoadIcon(hasCnText
            ? Weather.IconFileByText(d.Desc, d.IsDay)
            : Weather.IconFile(d.Code, d.IsDay));
        CurTemp.Text = d.Temp + "°";
        CurDesc.Text = hasCnText ? d.Desc : Weather.Desc(d.Code);
        CurHiLo.Text = "最高 " + d.Days[0].TMax + "° / 最低 " + d.Days[0].TMin + "°";

        MFeels.Text = d.Feels + "°";
        MHum.Text = d.Hum + "%";
        MWind.Text = string.IsNullOrEmpty(d.WindText)
            ? Weather.WindDir(d.WindDir) + " " + d.Wind
            : d.WindText;   // 中国天气网给的是「东北 4」这种现成文字
        MPrecip.Text = d.Precip.ToString("0.0") + " mm";
        MCloud.Text = d.Cloud + "%";
        MPress.Text = d.Pressure + "hPa";
        UpdateText.Text = "更新 " + d.Fetched;

        var hours = new List<HourCell>();
        for (int i = 0; i < d.Hours.Count && i < 8; i++)
        {
            var h = d.Hours[i];
            hours.Add(new HourCell
            {
                Time = h.HourOfDay.ToString("00") + "时",
                IconSource = LoadIcon(Weather.IconFile(h.Code, h.HourOfDay >= 6 && h.HourOfDay < 18)),
                Temp = h.Temp + "°",
                Pop = h.Pop + "%",
                TimeBrush = i == 0 ? CAcc : CSub,
            });
        }
        HoursList.ItemsSource = hours;

        // 六天温度条共用同一映射范围，否则各行温区长度互不可比
        var days = d.Days.Take(DayCount).ToList();
        double lo = days.Min(x => x.TMin), hi = days.Max(x => x.TMax);
        double span = hi - lo;
        var rows = new List<DayRow>();
        for (int i = 0; i < days.Count; i++)
        {
            var day = days[i];
            double x0 = span > 0 ? (day.TMin - lo) / span * (BarTrack - 4) : BarTrack / 2 - 2;
            double x1 = span > 0 ? (day.TMax - lo) / span * (BarTrack - 4) : BarTrack / 2 + 2;
            double mid = (x0 + x1) / 2;
            var date = DateTime.TryParse(day.Date, out var dt) ? dt : DateTime.Today.AddDays(i);
            rows.Add(new DayRow
            {
                Name = i == 0 ? "今天" : i == 1 ? "明天" : Weather.Weekday(date),
                IconSource = LoadIcon(string.IsNullOrEmpty(day.Text)
                    ? Weather.IconFile(day.Code)
                    : Weather.IconFileByText(day.Text)),
                Desc = string.IsNullOrEmpty(day.Text) ? Weather.Desc(day.Code) : day.Text,
                TMin = day.TMin + "°",
                TMax = day.TMax + "°",
                Pop = day.Pop + "%",
                NameBrush = i == 0 ? CAcc : CSub,
                BarBrush = i == 0 ? CAcc : CBar,
                DotBrush = i == 0 ? CDotToday : CDot,
                BarMargin = new Thickness(x0, 0, 0, 0),
                BarWidth = Math.Max(3, x1 - x0),
                DotMargin = new Thickness(mid - 2.5, 0, 0, 0),
            });
        }
        DaysList.ItemsSource = rows;

        DVis.Text = (d.Visibility / 1000.0).ToString("0.0") + "km";
        DDew.Text = d.Dew + "°";
        DGust.Text = d.Gust + "km/h";
        DUv.Text = d.Uv.ToString("0.#");
        DMoon.Text = Weather.MoonPhase(DateTime.TryParse(d.Days[0].Date, out var t0) ? t0 : DateTime.Today);
        DRise.Text = string.IsNullOrEmpty(d.Sunrise) ? "--" : d.Sunrise;
        DSet.Text = string.IsNullOrEmpty(d.Sunset) ? "--" : d.Sunset;
    }
}
