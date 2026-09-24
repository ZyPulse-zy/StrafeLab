using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using System.IO;
using System.Text.Json;
using StrafeLab.UI;
using StrafeLab.Core;

namespace StrafeLab;

public partial class MainWindow : Window
{
    private readonly RuntimeService _runtime = new();
    private bool _initialized;
    private readonly CancellationTokenSource _initializeStop=new();
    private bool _closing;
    private bool _closedReady;
    private readonly DispatcherTimer _refresh=new(){Interval=TimeSpan.FromMilliseconds(100)};

    public MainWindow()
    {
        InitializeComponent();
        _refresh.Tick+=(_,_)=>RefreshUi();
        _runtime.Notification += Runtime_Notification;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (_initialized) return;
        _initialized = true;

        dataPathText.Text = _runtime.DataDirectory;
        try
        {
            await _runtime.InitializeAsync(_initializeStop.Token);
            if(_closing)return;recordButton.IsEnabled=true;hallEnabledBox.IsEnabled=true;
            _refresh.Start();
            RefreshUi();
            if(Environment.GetEnvironmentVariable("STRAFELAB_TEST_MODE")=="1")
            {
                await Task.Delay(1200);UpdateLayout();
                var bitmap=new System.Windows.Media.Imaging.RenderTargetBitmap((int)ActualWidth,(int)ActualHeight,96,96,PixelFormats.Pbgra32);
                bitmap.Render(this);var encoder=new System.Windows.Media.Imaging.PngBitmapEncoder();encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                using(var file=File.Create(Path.Combine(_runtime.DataDirectory,"ui-preview.png")))encoder.Save(file);
                File.WriteAllText(Path.Combine(_runtime.DataDirectory,"smoke.json"),JsonSerializer.Serialize(new{initialized=true,gsiPort=_runtime.GsiPort,width=ActualWidth,height=ActualHeight}));
                Close();
            }
        }
        catch (OperationCanceledException) when(_closing) { }
        catch (Exception ex)
        {
            File.AppendAllText(Path.Combine(_runtime.DataDirectory,"errors.log"),ex+Environment.NewLine);
            topStatusText.Text = $"初始化失败：{ex.Message}";
            statusDot.Fill = new SolidColorBrush(Color.FromRgb(244, 122, 122));
        }
    }

    private async void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if(_closedReady)return;
        e.Cancel=true;if(_closing)return;_closing=true;_initializeStop.Cancel();_refresh.Stop();IsEnabled=false;
        try{await _runtime.DisposeAsync();}catch(Exception ex){File.AppendAllText(Path.Combine(_runtime.DataDirectory,"errors.log"),ex+Environment.NewLine);}
        _closedReady=true;Close();
    }

    private void Runtime_Notification(object? sender, string message)
    {
        Dispatcher.BeginInvoke(() =>
        {
            saveStatusText.Text = message;
            topStatusText.Text = message;
        });
    }

    private void RefreshUi()
    {
        var view = _runtime.GetView();
        var metrics = view.Metrics;
        topStatusText.Text = metrics.Status;
        statusDot.Fill = new SolidColorBrush(_runtime.IsRecording ? Color.FromRgb(99, 230, 168) : Color.FromRgb(127, 147, 176));
        diagnosticBox.IsEnabled=!_runtime.IsRecording;
        recordButton.Content = _runtime.IsRecording ? "停止并保存本局" : "开始记录";
        matchText.Text = metrics.CurrentMap == "—" ? "等待 CS2" : metrics.CurrentMap;
        weaponText.Text = $"武器 {metrics.CurrentWeapon} · 回合 {metrics.CurrentRound}";
        gapText.Text = $"{metrics.AverageGapMs:F1} ms";
        overlapText.Text = $"overlap {metrics.AverageOverlapMs:F1} ms";
        shotSpeedText.Text = metrics.ModelShotCount==0?"—":$"{metrics.AverageSpeedAtShot:F0} u/s";
        shotDeltaText.Text = $"timing {metrics.AverageShotDeltaMs:F1} ms";
        sampleText.Text = $"{metrics.TransitionCount}";
        windowText.Text = metrics.ModelShotCount==0?"低移速估计 · 等待模型样本":$"低移速 {metrics.FireWindowRate:P0} · {metrics.ModelShotCount} 样本";
        speedNowText.Text = $"{metrics.CurrentSpeed:F0} u/s";
        qualityText.Text = metrics.TransitionCount==0?"—":$"{metrics.ConfidenceRate:P0}";
        transitionCountText.Text = $"{metrics.TransitionCount} events · {metrics.ShotCount} shots";
        deviceChainText.Text = _runtime.HallStatus;
        gsiChainText.Text = $"{metrics.GsiStatus} · 127.0.0.1:{_runtime.GsiPort}";
        demoChainText.Text = metrics.DemoStatus;
        saveStatusText.Text = _runtime.IsRecording ? "自动保存中 · 每 5 秒写入" : "停止后自动保存并尝试匹配 Demo";
        speedChart.SetValues(metrics.SpeedHistory);

        transitionList.ItemsSource=view.Rows;
        hallStatusText.Text=$"{_runtime.HallReportCount:N0} 个 WASD 报告 · 固件键程，单位 mm";
        foreach(var pair in new[]{(InputControl.W,hallW,hallWText),(InputControl.A,hallA,hallAText),(InputControl.S,hallS,hallSText),(InputControl.D,hallD,hallDText)})
        {
            if(view.Hall.TryGetValue(pair.Item1,out var h)){pair.Item2.Value=h.Millimeters;pair.Item3.Text=$"{pair.Item1}  {h.Millimeters:F2} mm";}
        }
    }

    private async void Record_Click(object sender, RoutedEventArgs e)
    {
        if (_runtime.IsRecording)
        {
            _runtime.AutoRecord=false;autoRecordBox.IsChecked=false;
            recordButton.IsEnabled = false;
            await _runtime.StopRecordingAsync();
            recordButton.IsEnabled = true;
        }
        else
        {
            _runtime.StartRecording();
        }
        RefreshUi();
    }

    private void InstallGsi_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var paths = _runtime.InstallGsiConfig();
            saveStatusText.Text = paths.Count == 0 ? "未找到 CS2 cfg 目录" : $"已安装 {paths.Count} 个 GSI 配置";
        }
        catch (Exception ex)
        {
            saveStatusText.Text = $"GSI 安装失败：{ex.Message}";
        }
    }

    private void OpenData_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = _runtime.DataDirectory,
                UseShellExecute = true
            });
        }
        catch (Exception ex) { saveStatusText.Text = ex.Message; }
    }

    private void History_Click(object sender,RoutedEventArgs e) => new HistoryWindow(_runtime){Owner=this}.Show();
    private void Feedback_Click(object sender,RoutedEventArgs e)
    {
        MessageBox.Show(this,"训练时观察上方 WASD 键程及 gap/overlap。低移速窗口只估算移动误差，不代表后坐力已恢复。灯光反馈尚未针对当前固件验证，未启用。","训练提示");
    }
    private void Diagnostics_Click(object sender,RoutedEventArgs e)
    {
        var path=Path.Combine(_runtime.DataDirectory,"hid-inventory.json");
        File.WriteAllText(path,JsonSerializer.Serialize(new{inventory=_runtime.ProbeResult,hallStatus=_runtime.HallStatus,hallReports=_runtime.HallReportCount},new JsonSerializerOptions{WriteIndented=true}));
        Process.Start(new ProcessStartInfo(path){UseShellExecute=true});
    }
    private void Modes_Changed(object sender,RoutedEventArgs e)
    {
        if(!_initialized)return;
        _runtime.AutoRecord=autoRecordBox.IsChecked==true;
        _runtime.DiagnosticMode=diagnosticBox.IsChecked==true;
        diagnosticBox.IsEnabled=!_runtime.IsRecording;
    }
    private async void HallMode_Changed(object sender,RoutedEventArgs e)
    {
        if(!_initialized)return;hallEnabledBox.IsEnabled=false;
        try{await _runtime.SetHallEnabledAsync(hallEnabledBox.IsChecked==true);}
        finally{if(!_closing)hallEnabledBox.IsEnabled=true;}
    }
    private void SelectCfg_Click(object sender,RoutedEventArgs e)
    {
        var picker=new OpenFolderDialog{Title="选择 CS2 game/csgo/cfg 目录"};
        if(picker.ShowDialog(this)==true)try{_runtime.InstallGsiConfigAt(picker.FolderName);saveStatusText.Text="GSI 已写入，重启 CS2 后生效";}catch(Exception ex){MessageBox.Show(this,ex.Message);}
    }

    private void TitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ChangedButton == System.Windows.Input.MouseButton.Left) DragMove();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

}
