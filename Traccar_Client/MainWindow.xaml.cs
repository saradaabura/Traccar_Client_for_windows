using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Newtonsoft.Json;
using Microsoft.Toolkit.Uwp.Notifications;


namespace TraccarClientWPF
{
    public partial class MainWindow : Window
    {
        private const string ConfigFile = "config.json";
        private const string BufferFile = "offline_buffer.json";

        private AppConfig _config;
        private static readonly HttpClient HttpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        private DispatcherTimer _autoSendTimer;
        private bool _isExecuting = false;

        // タスクトレイ常駐用
        private System.Windows.Forms.NotifyIcon _notifyIcon;
        private bool _isReallyClosing = false;

        // Windows Power Status 構造体・API定義
        [StructLayout(LayoutKind.Sequential)]
        public struct SYSTEM_POWER_STATUS
        {
            public byte ACLineStatus;
            public byte BatteryFlag;
            public byte BatteryLifePercent;
            public byte SystemStatusFlag;
            public int BatteryLifeTime;
            public int BatteryFullLifeTime;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS lpSystemPowerStatus);

        public MainWindow()
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            InitializeComponent();
            _config = LoadConfig();
            ApplyConfigToUI();

            // --- タスクトレイアイコンの初期化 ---
            _notifyIcon = new System.Windows.Forms.NotifyIcon();
            _notifyIcon.Icon = System.Drawing.SystemIcons.Application;
            _notifyIcon.Visible = true;
            _notifyIcon.Text = "Traccar Client";

            // トレイアイコンをダブルクリックしたときウィンドウを復元
            _notifyIcon.DoubleClick += (s, args) => ShowWindow();

            // コンテキストメニュー（右クリックメニュー）の設定
            var contextMenu = new System.Windows.Forms.ContextMenuStrip();
            contextMenu.Items.Add("開く", null, (s, args) => ShowWindow());
            contextMenu.Items.Add("終了", null, (s, args) => {
                _isReallyClosing = true;
                Close();
            });
            _notifyIcon.ContextMenuStrip = contextMenu;
            // ----------------------------------

            // タイマーの初期化（自動送信用）
            _autoSendTimer = new DispatcherTimer();
            _autoSendTimer.Tick += async (s, e) => await ExecutePipelineAsync();

            UpdateBufferDisplay();

            // 起動時に自動スタートが有効な場合
            if (_config.AutoStart)
            {
                ChkAutoSend.IsChecked = true;
            }
        }

        // ウィンドウを再表示するヘルパーメソッド
        private void ShowWindow()
        {
            this.Show();
            this.WindowState = WindowState.Normal;
            this.Activate();
        }

        // 「×」ボタンが押されたときの挙動（トレイへ格納）
        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            if (!_isReallyClosing)
            {
                new ToastContentBuilder()
                    .AddText("Traccar Clientはバックグラウンドで実行しています。")
                    .AddText("終了するには、タスクトレイのメニューより「終了」を選択してください")
                    .Show();
                e.Cancel = true;
                this.Hide();
                return;
            }

            // 本当に終了する場合はトレイアイコンを破棄
            _notifyIcon.Dispose();
            base.OnClosing(e);
        }

        // ==================== 設定・バッファ管理 ====================
        private AppConfig LoadConfig()
        {
            var defaultConfig = new AppConfig
            {
                TraccarUrl = "http://example.com:5055",
                DeviceId = "123456",
                Interval = 30,
                AutoStart = false
            };

            try
            {
                if (File.Exists(ConfigFile))
                {
                    string json = File.ReadAllText(ConfigFile, Encoding.UTF8);
                    var loaded = JsonConvert.DeserializeObject<AppConfig>(json);
                    if (loaded != null) return loaded;
                }
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"config.json 読み込み失敗: {ex.Message}");
                SaveConfig(defaultConfig);
            }
            return defaultConfig;
        }

        private void SaveConfig(AppConfig config)
        {
            try
            {
                string json = JsonConvert.SerializeObject(config, Newtonsoft.Json.Formatting.Indented);
                File.WriteAllText(ConfigFile, json, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"config.json 保存エラー: {ex.Message}");
            }
        }

        private void ApplyConfigToUI()
        {
            TxtUrl.Text = _config.TraccarUrl;
            TxtDeviceId.Text = _config.DeviceId;
            TxtInterval.Text = _config.Interval.ToString();
            ChkAutoStart.IsChecked = _config.AutoStart;
        }

        private List<WifiRecord> LoadBuffer()
        {
            if (!File.Exists(BufferFile)) return new List<WifiRecord>();
            try
            {
                string json = File.ReadAllText(BufferFile, Encoding.UTF8);
                return JsonConvert.DeserializeObject<List<WifiRecord>>(json) ?? new List<WifiRecord>();
            }
            catch
            {
                return new List<WifiRecord>();
            }
        }

        private void SaveBuffer(List<WifiRecord> records)
        {
            try
            {
                string json = JsonConvert.SerializeObject(records, Newtonsoft.Json.Formatting.Indented);
                File.WriteAllText(BufferFile, json, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"バッファ保存エラー: {ex.Message}");
            }
        }

        private void UpdateBufferDisplay()
        {
            var records = LoadBuffer();
            int count = records.Count;
            TxtBufferCount.Text = $"未送信データ: {count}件";
            TxtBufferCount.Foreground = count > 0 ? System.Windows.Media.Brushes.Orange : System.Windows.Media.Brushes.Gray;
        }

        // ==================== ハードウェア・OS情報取得 ====================
        private (int? batt, bool? charging) GetBatteryInfo()
        {
            try
            {
                if (GetSystemPowerStatus(out var status))
                {
                    int percent = status.BatteryLifePercent;
                    bool charging = (status.ACLineStatus == 1);
                    if (percent != 255)
                    {
                        return (percent, charging);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"バッテリー情報取得エラー: {ex.Message}");
            }
            return (null, null);
        }

        private List<WifiAccessPoint> GetWifiNetworks()
        {
            var wifiList = new List<WifiAccessPoint>();
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "netsh",
                    Arguments = "wlan show networks mode=bssid",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.GetEncoding(932) // Shift-JIS対応
                };

                using var process = Process.Start(startInfo);
                if (process == null) return wifiList;
                string output = process.StandardOutput.ReadToEnd();
                process.WaitForExit();

                // ノーブレークスペース(\u00A0)を通常の半角スペースに置換してパースを安定させる
                output = output.Replace('\u00A0', ' ');

                string currentBssid = null;
                int currentSignal = -70; // デフォルト値

                foreach (var rawLine in output.Split('\n'))
                {
                    string line = rawLine.Trim();

                    // BSSID / 物理アドレスの検出
                    var bssidMatch = Regex.Match(line, @"(?:BSSID|物理アドレス)\s*\d*\s*:\s*([0-9a-fA-F:]{17})", RegexOptions.IgnoreCase);
                    if (bssidMatch.Success)
                    {
                        if (!string.IsNullOrEmpty(currentBssid))
                        {
                            wifiList.Add(new WifiAccessPoint { MacAddress = currentBssid, SignalStrength = currentSignal });
                        }
                        currentBssid = bssidMatch.Groups[1].Value;
                        currentSignal = -70;
                        continue;
                    }

                    // シグナルの検出
                    var signalMatch = Regex.Match(line, @"(?:信号|Signal)\s*:\s*(\d+)%", RegexOptions.IgnoreCase);
                    if (signalMatch.Success && int.TryParse(signalMatch.Groups[1].Value, out int quality))
                    {
                        currentSignal = (quality / 2) - 100;
                        if (!string.IsNullOrEmpty(currentBssid))
                        {
                            wifiList.Add(new WifiAccessPoint { MacAddress = currentBssid, SignalStrength = currentSignal });
                            currentBssid = null;
                        }
                    }
                }

                // 残っているBSSIDがあれば追加
                if (!string.IsNullOrEmpty(currentBssid))
                {
                    wifiList.Add(new WifiAccessPoint { MacAddress = currentBssid, SignalStrength = currentSignal });
                }

                // 強力なフォールバック：構造解析で取れなかった場合でも出力からすべてのMACアドレスを強制抽出する
                if (wifiList.Count == 0)
                {
                    var matches = Regex.Matches(output, @"([0-9a-fA-F]{2}:[0-9a-fA-F]{2}:[0-9a-fA-F]{2}:[0-9a-fA-F]{2}:[0-9a-fA-F]{2}:[0-9a-fA-F]{2})");
                    foreach (Match m in matches)
                    {
                        string mac = m.Value;
                        if (!wifiList.Exists(w => w.MacAddress.Equals(mac, StringComparison.OrdinalIgnoreCase)))
                        {
                            wifiList.Add(new WifiAccessPoint { MacAddress = mac, SignalStrength = -70 });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"スキャンエラー: {ex.Message}");
            }
            return wifiList;
        }

        private async Task<bool> IsOnlineAsync()
        {
            try
            {
                var response = await HttpClient.GetAsync("https://www.google.com");
                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        // ==================== メインパイプライン ====================
        private async Task ExecutePipelineAsync()
        {
            if (_isExecuting) return;
            _isExecuting = true;

            try
            {
                string url = _config.TraccarUrl;
                string devId = _config.DeviceId;

                TxtStatus.Text = "Wi-Fiをスキャン中...";
                StatusProgressBar.Value = 25;
                StatusProgressBar.Foreground = new SolidColorBrush(Colors.Green);
                TxtStatus.Foreground = System.Windows.Media.Brushes.Blue;

                var wifiAps = GetWifiNetworks();
                long currentTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var (batt, charging) = GetBatteryInfo();

                if (wifiAps.Count == 0)
                {
                    TxtStatus.Text = "エラー: 周囲のWi-Fi(BSSID)が検出できませんでした";
                    TxtStatus.Foreground = System.Windows.Media.Brushes.Red;
                    TxtLog.Text = "Wi-Fi機能およびOSの位置情報許可を確認してください。";
                    return;
                }

                var currentRecord = new WifiRecord
                {
                    Timestamp = currentTimestamp,
                    WifiAps = wifiAps,
                    Batt = batt,
                    Charging = charging
                };

                var records = LoadBuffer();
                records.Add(currentRecord);
                SaveBuffer(records);
                UpdateBufferDisplay();

                TxtStatus.Text = $"ネットワーク接続を確認中... (総データ: {records.Count}件)";
                StatusProgressBar.Value = 50;

                if (!await IsOnlineAsync())
                {
                    TxtStatus.Text = $"オフラインのためバッファに保存しました (未送信: {records.Count}件)";
                    StatusProgressBar.Foreground = new SolidColorBrush(Colors.Orange);
                    TxtStatus.Foreground = System.Windows.Media.Brushes.Orange;
                    TxtLog.Text = $"時刻: {currentTimestamp}\n現在オフラインのため、通信回復時にまとめて送信します。";
                    return;
                }

                var failedRecords = new List<WifiRecord>();
                int successCount = 0;
                string lastLog = "";

                TxtStatus.Text = $"BeaconDB照合 & Traccar送信中... (全{records.Count}件)";
                StatusProgressBar.Value = 75;

                for (int i = 0; i < records.Count; i++)
                {
                    TxtStatus.Text = $"送信中... ({i + 1}/{records.Count}件)";
                    var rec = records[i];

                    // 1. BeaconDBへ照会
                    var (lat, lon, accuracy, errMessage) = await GetLocationFromBeaconDbAsync(rec.WifiAps);
                    if (lat == null || lon == null)
                    {
                        failedRecords.Add(rec);
                        lastLog = $"位置特定エラー: {errMessage}";
                        continue;
                    }

                    // 2. Traccarへ送信
                    var (success, traccarMsg) = await SendToTraccarAsync(url, devId, lat.Value, lon.Value, rec.Timestamp, rec.Batt, rec.Charging, accuracy);
                    if (success)
                    {
                        successCount++;
                        string accStr = accuracy.HasValue ? $"{accuracy.Value:F1}m" : "不明";
                        lastLog = $"緯度: {lat}\n経度: {lon}\n精度: {accStr}\n{traccarMsg}";
                    }
                    else
                    {
                        failedRecords.Add(rec);
                        lastLog = $"送信エラー: {traccarMsg}";
                    }
                }

                SaveBuffer(failedRecords);
                UpdateBufferDisplay();

                if (failedRecords.Count > 0)
                {
                    
                    if (successCount > 0)
                    {
                        TxtStatus.Text = $"送信完了（一部失敗: {failedRecords.Count}件を再バッファ化）";
                        TxtStatus.Foreground = System.Windows.Media.Brushes.Orange;
                        StatusProgressBar.Foreground = new SolidColorBrush(Colors.Orange);
                    }
                    else
                    {
                        TxtStatus.Text = $"送信失敗（{failedRecords.Count}件をバッファに保存しました）";
                        TxtStatus.Foreground = System.Windows.Media.Brushes.Red;
                        StatusProgressBar.Foreground = new SolidColorBrush(Colors.Red);
                    }
                }
                else
                {
                    StatusProgressBar.Value = 100;
                    TxtStatus.Text = $"全{successCount}件の送信完了！";
                    TxtStatus.Foreground = System.Windows.Media.Brushes.Green;
                }

                TxtLog.Text = $"最新のログ:\n{lastLog}";
            }
            catch (Exception ex)
            {
                TxtStatus.Text = "パイプライン実行時エラー";
                TxtStatus.Foreground = System.Windows.Media.Brushes.Red;
                TxtLog.Text = ex.ToString();
            }
            finally
            {
                _isExecuting = false;
            }
        }

        private async Task<(double? lat, double? lon, double? accuracy, string errorMsg)> GetLocationFromBeaconDbAsync(List<WifiAccessPoint> wifiAps)
        {
            string url = "https://api.beacondb.net/v1/geolocate";
            var payload = new { wifiAccessPoints = wifiAps };
            try
            {
                string json = JsonConvert.SerializeObject(payload);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                var response = await HttpClient.PostAsync(url, content);
                string responseString = await response.Content.ReadAsStringAsync();

                var result = JsonConvert.DeserializeObject<BeaconDbResponse>(responseString);
                if (result?.Location != null)
                {
                    return (result.Location.Lat, result.Location.Lng, result.Accuracy, null);
                }
                return (null, null, null, $"位置特定不可: {responseString}");
            }
            catch (Exception ex)
            {
                return (null, null, null, $"BeaconDB通信エラー: {ex.Message}");
            }
        }

        private async Task<(bool success, string message)> SendToTraccarAsync(string traccarUrl, string deviceId, double lat, double lon, long timestamp, int? batt, bool? charging, double? accuracy)
        {
            var query = new Dictionary<string, string>
            {
                { "id", deviceId },
                { "lat", lat.ToString(System.Globalization.CultureInfo.InvariantCulture) },
                { "lon", lon.ToString(System.Globalization.CultureInfo.InvariantCulture) },
                { "timestamp", timestamp.ToString() }
            };

            if (accuracy.HasValue) query["accuracy"] = accuracy.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (batt.HasValue) query["batt"] = batt.Value.ToString();
            if (charging.HasValue) query["charge"] = charging.Value ? "true" : "false";

            var builder = new UriBuilder(traccarUrl);
            var queryBuilder = System.Web.HttpUtility.ParseQueryString(string.Empty);
            foreach (var kv in query) queryBuilder[kv.Key] = kv.Value;
            builder.Query = queryBuilder.ToString();

            try
            {
                var request = new HttpRequestMessage(HttpMethod.Get, builder.Uri);
                request.Headers.ConnectionClose = true; // Connection: close
                var response = await HttpClient.SendAsync(request);

                if (response.IsSuccessStatusCode)
                {
                    string extraInfo = $"時刻: {timestamp}";
                    if (accuracy.HasValue) extraInfo += $" | 精度: {accuracy}m";
                    if (batt.HasValue) extraInfo += $" | バッテリー: {batt}% ({(charging == true ? "充電中" : "放電中")})";

                    return (true, $"Traccar送信成功 (Status: {(int)response.StatusCode})\n{extraInfo}");
                }
                else
                {
                    return (false, $"Traccar送信エラー: HTTP {(int)response.StatusCode}");
                }
            }
            catch (Exception ex)
            {
                return (false, $"Traccar通信エラー: {ex.Message}");
            }
        }

        // ==================== イベントハンドラ ====================
        private void BtnSaveConfig_Click(object sender, RoutedEventArgs e)
        {
            if (!int.TryParse(TxtInterval.Text, out int intervalVal) || intervalVal < 5)
            {
                intervalVal = 5;
                TxtInterval.Text = "5";
            }

            _config.TraccarUrl = TxtUrl.Text.Trim();
            _config.DeviceId = TxtDeviceId.Text.Trim();
            _config.Interval = intervalVal;
            _config.AutoStart = ChkAutoStart.IsChecked ?? false;

            SaveConfig(_config);
            System.Windows.MessageBox.Show("設定を保存しました", "情報", MessageBoxButton.OK, MessageBoxImage.Information);

            // 自動送信中ならタイマー間隔を更新
            if (_autoSendTimer.IsEnabled)
            {
                _autoSendTimer.Interval = TimeSpan.FromSeconds(_config.Interval);
            }
        }

        private async void BtnManual_Click(object sender, RoutedEventArgs e)
        {
            BtnManual.IsEnabled = false;
            await ExecutePipelineAsync();
            BtnManual.IsEnabled = true;
        }

        private void ChkAutoSend_Checked(object sender, RoutedEventArgs e)
        {
            _autoSendTimer.Interval = TimeSpan.FromSeconds(_config.Interval);
            _autoSendTimer.Start();
            BtnManual.IsEnabled = false;
            TxtStatus.Text = "自動送信を開始しました";
            TxtStatus.Foreground = System.Windows.Media.Brushes.Gray;
        }

        private void ChkAutoSend_Unchecked(object sender, RoutedEventArgs e)
        {
            _autoSendTimer.Stop();
            BtnManual.IsEnabled = true;
            TxtStatus.Text = "自動送信を停止しました";
            TxtStatus.Foreground = System.Windows.Media.Brushes.Gray;
        }

    }

    // ==================== データモデルクラス ====================
    public class AppConfig
    {
        [JsonProperty("traccar_url")]
        public string TraccarUrl { get; set; } = string.Empty;

        [JsonProperty("device_id")]
        public string DeviceId { get; set; } = string.Empty;

        [JsonProperty("interval")]
        public int Interval { get; set; } = 30;

        [JsonProperty("auto_start")]
        public bool AutoStart { get; set; }
    }

    public class WifiRecord
    {
        [JsonProperty("timestamp")]
        public long Timestamp { get; set; }

        [JsonProperty("wifi_aps")]
        public List<WifiAccessPoint> WifiAps { get; set; } = new();

        [JsonProperty("batt")]
        public int? Batt { get; set; }

        [JsonProperty("charging")]
        public bool? Charging { get; set; }
    }

    public class WifiAccessPoint
    {
        [JsonProperty("macAddress")]
        public string MacAddress { get; set; } = string.Empty;

        [JsonProperty("signalStrength")]
        public int SignalStrength { get; set; }
    }

    public class BeaconDbResponse
    {
        [JsonProperty("location")]
        public LocationData? Location { get; set; }

        [JsonProperty("accuracy")]
        public double? Accuracy { get; set; }
    }

    public class LocationData
    {
        [JsonProperty("lat")]
        public double Lat { get; set; }

        [JsonProperty("lng")]
        public double Lng { get; set; }
    }
}