using System.Text.Json;
using LibreHardwareMonitor.Hardware;
using System.Globalization;
using System.Text.RegularExpressions;

DotEnv.Load();

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

var app = builder.Build();

app.UseCors();

var monitor = new HardwareMonitorService();
monitor.Start();

app.MapGet("/", () => "Monitor server is running. Open /stats, /health, or /cooler");

app.MapGet("/health", () => Results.Json(new
{
    status = "ok",
    updatedAt = DateTimeOffset.Now
}));

app.MapGet("/stats", () =>
{
    var data = monitor.GetStats();
    return Results.Json(data, new JsonSerializerOptions
    {
        WriteIndented = true
    });
});

// IMPORTANT:
// Cooler is separated from /stats so Wallpaper Engine does not poll it every second.
// It reads iCUE's sensor CSV log instead of touching Corsair USB devices directly.
app.MapGet("/cooler", () =>
{
    var data = HardwareMonitorService.GetCoolerStatsCached();
    return Results.Json(data, new JsonSerializerOptions
    {
        WriteIndented = true
    });
});

app.Run("http://127.0.0.1:8765");

internal static class DotEnv
{
    public static void Load()
    {
        var envPath = FindEnvFile();
        if (envPath is null)
        {
            return;
        }

        foreach (var rawLine in File.ReadLines(envPath))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            if (line.StartsWith("export ", StringComparison.OrdinalIgnoreCase))
            {
                line = line[7..].TrimStart();
            }

            var separator = line.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            if (value.Length >= 2 &&
                ((value[0] == '"' && value[^1] == '"') ||
                 (value[0] == '\'' && value[^1] == '\'')))
            {
                value = value[1..^1];
            }

            if (Environment.GetEnvironmentVariable(key) is null)
            {
                Environment.SetEnvironmentVariable(key, value);
            }
        }
    }

    private static string? FindEnvFile()
    {
        var starts = new[] { Environment.CurrentDirectory, AppContext.BaseDirectory };
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var start in starts)
        {
            var directory = new DirectoryInfo(start);
            while (directory is not null && visited.Add(directory.FullName))
            {
                var candidate = Path.Combine(directory.FullName, ".env");
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                directory = directory.Parent;
            }
        }

        return null;
    }
}

public record SensorDto(
    string Hardware,
    string HardwareId,
    string HardwareType,
    string Sensor,
    string SensorType,
    float Value
);

public class HardwareMonitorService
{
    private readonly Computer _computer;

    // Prevent concurrent /stats requests from updating LibreHardwareMonitor at the same time.
    private readonly object _statsLock = new();

    // Cooler / iCUE sensor log cache settings
    private static readonly object _coolerLock = new();
    private static object? _coolerCache = null;
    private static DateTimeOffset _coolerLastUpdate = DateTimeOffset.MinValue;
    private static DateTimeOffset _coolerCacheReadAt = DateTimeOffset.MinValue;
    private static readonly string _icueLogDirectory = ReadIcueLogDirectory();
    private static readonly float _coolerFanMaxRpm = ReadCoolerFanMaxRpm();
    private static readonly float _coolerPumpMaxRpm = ReadCoolerPumpMaxRpm();

    private static readonly bool _coolerPollingEnabled =
        !string.Equals(
            Environment.GetEnvironmentVariable("MONITOR_ENABLE_COOLER"),
            "false",
            StringComparison.OrdinalIgnoreCase
        );

    // Cooler polling uses adaptive throttling: fresh enough when healthy, conservative after trouble.
    private static readonly TimeSpan _coolerBaseRefreshInterval =
        TimeSpan.FromSeconds(ReadCoolerRefreshSeconds());
    private static readonly TimeSpan _coolerBackoffInterval =
        TimeSpan.FromSeconds(ReadCoolerBackoffSeconds());
    private static TimeSpan _coolerCurrentRefreshInterval = _coolerBaseRefreshInterval;
    private static int _coolerConsecutiveFailures = 0;

    public HardwareMonitorService()
    {
        _computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMemoryEnabled = true,
            IsMotherboardEnabled = true,
            IsStorageEnabled = true,
            IsNetworkEnabled = true,
            IsControllerEnabled = true
        };
    }

    public void Start()
    {
        _computer.Open();
    }

    public object GetStats()
    {
        lock (_statsLock)
        {
            return BuildStats();
        }
    }

    private object BuildStats()
    {
        foreach (var hardware in _computer.Hardware)
        {
            try
            {
                hardware.Update();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WARN] Failed to update hardware: {hardware.Name} / {hardware.HardwareType} - {ex.Message}");
                continue;
            }

            foreach (var subHardware in hardware.SubHardware)
            {
                try
                {
                    subHardware.Update();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[WARN] Failed to update sub-hardware: {subHardware.Name} / {subHardware.HardwareType} - {ex.Message}");
                }
            }
        }

        var sensors = GetAllSensors()
            .Where(s => s.Value.HasValue)
            .Select(s => new SensorDto(
                Hardware: s.Hardware.Name,
                HardwareId: s.Hardware.Identifier.ToString(),
                HardwareType: s.Hardware.HardwareType.ToString(),
                Sensor: s.Name,
                SensorType: s.SensorType.ToString(),
                Value: (float)Math.Round(s.Value!.Value, 2)
            ))
            .ToList();

        float? Find(string hardwareType, string sensorType, params string[] sensorNames)
        {
            foreach (var name in sensorNames)
            {
                var found = sensors.FirstOrDefault(s =>
                    string.Equals(s.HardwareType, hardwareType, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(s.SensorType, sensorType, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(s.Sensor, name, StringComparison.OrdinalIgnoreCase)
                );

                if (found != null)
                {
                    return found.Value;
                }
            }

            return null;
        }

        float? FindByHardwareName(string hardwareName, string sensorType, params string[] sensorNames)
        {
            foreach (var name in sensorNames)
            {
                var found = sensors.FirstOrDefault(s =>
                    string.Equals(s.Hardware, hardwareName, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(s.SensorType, sensorType, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(s.Sensor, name, StringComparison.OrdinalIgnoreCase)
                );

                if (found != null)
                {
                    return found.Value;
                }
            }

            return null;
        }

        var storageLabels = new Dictionary<string, string>
        {
            ["/nvme/0"] = "(E:)",
            ["/nvme/2"] = "(F:)",
            ["/nvme/1"] = "(C:)"
        };

        var storage = sensors
            .Where(s => s.HardwareType == "Storage")
            .GroupBy(s => s.HardwareId)
            .Select((g, index) =>
            {
                var id = g.Key;
                var first = g.FirstOrDefault();

                return new
                {
                    id,
                    name = storageLabels.TryGetValue(id, out var label)
                        ? label
                        : $"SN850X #{index + 1}",

                    rawName = first?.Hardware ?? "Unknown Storage",

                    temp = g.FirstOrDefault(s =>
                        s.SensorType == "Temperature" &&
                        s.Sensor.Contains("Composite", StringComparison.OrdinalIgnoreCase)
                    )?.Value,

                    used = g.FirstOrDefault(s =>
                        s.SensorType == "Load" &&
                        s.Sensor.Contains("Used Space", StringComparison.OrdinalIgnoreCase)
                    )?.Value,

                    life = g.FirstOrDefault(s =>
                        s.SensorType == "Level" &&
                        s.Sensor == "Life"
                    )?.Value,

                    freeGb = g.FirstOrDefault(s =>
                        s.SensorType == "Data" &&
                        s.Sensor == "Free Space"
                    )?.Value,

                    totalGb = g.FirstOrDefault(s =>
                        s.SensorType == "Data" &&
                        s.Sensor == "Total Space"
                    )?.Value
                };
            })
            .OrderBy(d => d.id)
            .ToList();

        var network = sensors
            .Where(s => s.HardwareType == "Network")
            .GroupBy(s => s.Hardware)
            .Select(g => new
            {
                name = g.Key,
                uploadKb = g.FirstOrDefault(s =>
                    s.SensorType == "Throughput" &&
                    s.Sensor.Contains("Upload Speed", StringComparison.OrdinalIgnoreCase)
                )?.Value,
                downloadKb = g.FirstOrDefault(s =>
                    s.SensorType == "Throughput" &&
                    s.Sensor.Contains("Download Speed", StringComparison.OrdinalIgnoreCase)
                )?.Value
            })
            .ToList();

        var activeNetwork = network
            .Where(n => (n.uploadKb ?? 0) > 0 || (n.downloadKb ?? 0) > 0)
            .OrderByDescending(n => (n.uploadKb ?? 0) + (n.downloadKb ?? 0))
            .FirstOrDefault()
            ?? network.FirstOrDefault(n =>
                n.name.Contains("Wi-Fi", StringComparison.OrdinalIgnoreCase)
            )
            ?? network.FirstOrDefault(n =>
                n.name.Contains("Ethernet", StringComparison.OrdinalIgnoreCase)
            )
            ?? network.FirstOrDefault();

        var motherboardTempLabels = new Dictionary<string, string>
        {
            ["Temperature #1"] = "System 1",
            ["Temperature #2"] = "PCH",
            ["Temperature #3"] = "CPU Socket",
            ["Temperature #4"] = "PCIEX16",
            ["Temperature #5"] = "VRM MOS",
            ["Temperature #6"] = "VCORE MOS"
        };

        var motherboardFanLabels = new Dictionary<string, string>
        {
            ["Fan #3"] = "M.2 / SSD Fan"
        };

        var motherboardTemps = motherboardTempLabels
            .Select(x => new
            {
                id = x.Key,
                name = x.Value,
                value = Find("SuperIO", "Temperature", x.Key)
            })
            .ToList();

        var motherboardFans = motherboardFanLabels
            .Select(x => new
            {
                id = x.Key,
                name = x.Value,
                rpm = Find("SuperIO", "Fan", x.Key)
            })
            .Where(f => (f.rpm ?? 0) > 0)
            .ToList();

        return new
        {
            updatedAt = DateTimeOffset.Now,

            cpu = new
            {
                temp = Find("Cpu", "Temperature", "Core (Tctl/Tdie)", "CPU Package", "Core Max", "CCD1 (Tdie)"),
                ccd = Find("Cpu", "Temperature", "CCD1 (Tdie)"),
                load = Find("Cpu", "Load", "CPU Total"),
                power = Find("Cpu", "Power", "Package"),
                clock = Find("Cpu", "Clock", "Cores (Average)")
            },

            gpu = new
            {
                temp = Find("GpuNvidia", "Temperature", "GPU Core"),
                hotspot = Find("GpuNvidia", "Temperature", "GPU Hot Spot"),
                memoryTemp = Find("GpuNvidia", "Temperature", "GPU Memory Junction"),
                load = Find("GpuNvidia", "Load", "GPU Core"),
                power = Find("GpuNvidia", "Power", "GPU Package"),
                vramUsedMb = Find("GpuNvidia", "SmallData", "GPU Memory Used"),
                vramTotalMb = Find("GpuNvidia", "SmallData", "GPU Memory Total"),
                vramFreeMb = Find("GpuNvidia", "SmallData", "GPU Memory Free"),
                vramUsedGb = Math.Round((Find("GpuNvidia", "SmallData", "GPU Memory Used") ?? 0) / 1024, 2),
                vramTotalGb = Math.Round((Find("GpuNvidia", "SmallData", "GPU Memory Total") ?? 0) / 1024, 2),
                fan1Rpm = Find("GpuNvidia", "Fan", "GPU Fan 1"),
                fan2Rpm = Find("GpuNvidia", "Fan", "GPU Fan 2")
            },

            ram = new
            {
                load = FindByHardwareName("Total Memory", "Load", "Memory"),
                usedGb = FindByHardwareName("Total Memory", "Data", "Memory Used"),
                availableGb = FindByHardwareName("Total Memory", "Data", "Memory Available"),
                totalGb = Math.Round(
                    (FindByHardwareName("Total Memory", "Data", "Memory Used") ?? 0) +
                    (FindByHardwareName("Total Memory", "Data", "Memory Available") ?? 0),
                    2
                ),
                dimm0Temp = Find("Memory", "Temperature", "DIMM #0"),
                dimm2Temp = Find("Memory", "Temperature", "DIMM #2")
            },

            motherboard = new
            {
                temps = motherboardTemps,
                fans = motherboardFans
            },

            storage,
            activeNetwork
        };
    }

    private IEnumerable<ISensor> GetAllSensors()
    {
        foreach (var hardware in _computer.Hardware)
        {
            if (hardware?.Sensors != null)
            {
                foreach (var sensor in hardware.Sensors)
                {
                    if (sensor != null)
                    {
                        yield return sensor;
                    }
                }
            }

            if (hardware?.SubHardware != null)
            {
                foreach (var subHardware in hardware.SubHardware)
                {
                    if (subHardware?.Sensors == null)
                    {
                        continue;
                    }

                    foreach (var sensor in subHardware.Sensors)
                    {
                        if (sensor != null)
                        {
                            yield return sensor;
                        }
                    }
                }
            }
        }
    }

    public static object GetCoolerStatsCached()
    {
        lock (_coolerLock)
        {
            if (!_coolerPollingEnabled)
            {
                return new
                {
                    name = "Corsair iCUE H100i Elite RGB",
                    status = "disabled",
                    source = "icue-sensor-log",
                    warning = "Cooler polling is disabled. Remove MONITOR_ENABLE_COOLER=false or set MONITOR_ENABLE_COOLER=true to enable it."
                };
            }

            if (_coolerCache != null &&
                DateTimeOffset.Now - _coolerCacheReadAt < _coolerCurrentRefreshInterval)
            {
                return _coolerCache;
            }

            var result = ReadCoolerOnce();

            if (result.IsValid)
            {
                _coolerConsecutiveFailures = 0;
                _coolerCurrentRefreshInterval = _coolerBaseRefreshInterval;

                _coolerCache = new
                {
                    name = "Corsair iCUE H100i Elite RGB",
                    status = "ok",
                    updatedAt = result.UpdatedAt ?? DateTimeOffset.Now,
                    source = "icue-sensor-log",
                    logFile = result.LogFile,
                    refreshIntervalSeconds = _coolerCurrentRefreshInterval.TotalSeconds,
                    consecutiveFailures = _coolerConsecutiveFailures,
                    liquidTemp = result.LiquidTemp,
                    fan1Rpm = result.Fan1Rpm,
                    fan1Duty = result.Fan1Duty,
                    fan2Rpm = result.Fan2Rpm,
                    fan2Duty = result.Fan2Duty,
                    pumpRpm = result.PumpRpm,
                    pumpDuty = result.PumpDuty
                };

                _coolerLastUpdate = result.UpdatedAt ?? DateTimeOffset.Now;
                _coolerCacheReadAt = DateTimeOffset.Now;
                return _coolerCache;
            }

            _coolerConsecutiveFailures++;
            _coolerCurrentRefreshInterval = _coolerBackoffInterval;

            if (_coolerCache != null)
            {
                return new
                {
                    name = "Corsair iCUE H100i Elite RGB",
                    status = "stale",
                    updatedAt = _coolerLastUpdate,
                    source = "icue-sensor-log",
                    refreshIntervalSeconds = _coolerCurrentRefreshInterval.TotalSeconds,
                    consecutiveFailures = _coolerConsecutiveFailures,
                    warning = "Latest read failed or returned invalid values. Returning last known good data.",
                    data = _coolerCache
                };
            }

            return new
            {
                name = "Corsair iCUE H100i Elite RGB",
                status = "error",
                source = "icue-sensor-log",
                refreshIntervalSeconds = _coolerCurrentRefreshInterval.TotalSeconds,
                consecutiveFailures = _coolerConsecutiveFailures,
                error = result.Error
            };
        }
    }

    private static int ReadCoolerRefreshSeconds()
    {
        var raw = Environment.GetEnvironmentVariable("MONITOR_COOLER_REFRESH_SECONDS");

        if (int.TryParse(raw, out var seconds))
        {
            return Math.Clamp(seconds, 5, 3600);
        }

        return 5;
    }

    private static int ReadCoolerBackoffSeconds()
    {
        var raw = Environment.GetEnvironmentVariable("MONITOR_COOLER_BACKOFF_SECONDS");

        if (int.TryParse(raw, out var seconds))
        {
            return Math.Clamp(seconds, 60, 3600);
        }

        return 300;
    }

    private static float ReadCoolerFanMaxRpm()
    {
        var raw = Environment.GetEnvironmentVariable("MONITOR_COOLER_FAN_MAX_RPM");

        if (float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var rpm))
        {
            return Math.Clamp(rpm, 500, 5000);
        }

        return 2000;
    }

    private static float ReadCoolerPumpMaxRpm()
    {
        var raw = Environment.GetEnvironmentVariable("MONITOR_COOLER_PUMP_MAX_RPM");

        if (float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var rpm))
        {
            return Math.Clamp(rpm, 1000, 6000);
        }

        return 2850;
    }

    private static string ReadIcueLogDirectory()
    {
        var raw = Environment.GetEnvironmentVariable("MONITOR_ICUE_LOG_DIR");

        if (!string.IsNullOrWhiteSpace(raw))
        {
            return raw;
        }

        return Path.Combine(AppContext.BaseDirectory, "icue_logs");
    }

    private record CoolerReadResult(
        bool IsValid,
        DateTimeOffset? UpdatedAt,
        string? LogFile,
        float? LiquidTemp,
        float? Fan1Rpm,
        float? Fan1Duty,
        float? Fan2Rpm,
        float? Fan2Duty,
        float? PumpRpm,
        float? PumpDuty,
        string? Error
    );

    private static CoolerReadResult ReadCoolerOnce()
    {
        try
        {
            if (!Directory.Exists(_icueLogDirectory))
            {
                return new CoolerReadResult(
                    false,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    $"iCUE log directory does not exist: {_icueLogDirectory}"
                );
            }

            var latestLogs = Directory
                .EnumerateFiles(_icueLogDirectory, "*.csv", SearchOption.TopDirectoryOnly)
                .Select(path => new FileInfo(path))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Take(5)
                .ToList();

            if (latestLogs.Count == 0)
            {
                return new CoolerReadResult(
                    false,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    $"No iCUE CSV log files found in {_icueLogDirectory}."
                );
            }

            IcueCsvRow? row = null;
            FileInfo? sourceLog = null;

            foreach (var log in latestLogs)
            {
                row = ReadLatestIcueRow(log.FullName);

                if (row != null)
                {
                    sourceLog = log;
                    break;
                }
            }

            if (row == null || sourceLog == null)
            {
                return new CoolerReadResult(
                    false,
                    null,
                    latestLogs[0].FullName,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    $"No complete iCUE sensor rows found in the latest CSV logs under {_icueLogDirectory}."
                );
            }

            var liquidTemp = ParseSensorFloat(row.Columns, "H100i ELITE Coolant Temp");
            var fan1Rpm = ParseSensorFloat(row.Columns, "H100i ELITE Fan #1");
            var fan2Rpm = ParseSensorFloat(row.Columns, "H100i ELITE Fan #2");
            var pumpRpm = ParseSensorFloat(row.Columns, "H100i ELITE Pump");
            var updatedAt = ParseIcueTimestamp(row.Timestamp);
            var fan1Duty = EstimateDuty(fan1Rpm, _coolerFanMaxRpm);
            var fan2Duty = EstimateDuty(fan2Rpm, _coolerFanMaxRpm);
            var pumpDuty = EstimateDuty(pumpRpm, _coolerPumpMaxRpm);

            bool valid =
                liquidTemp is >= 15 and <= 70 &&
                fan1Rpm is >= 0 and <= 4000 &&
                fan2Rpm is >= 0 and <= 4000 &&
                pumpRpm is >= 1000 and <= 5000;

            if (!valid)
            {
                return new CoolerReadResult(
                    false,
                    updatedAt,
                    sourceLog.FullName,
                    liquidTemp,
                    fan1Rpm,
                    fan1Duty,
                    fan2Rpm,
                    fan2Duty,
                    pumpRpm,
                    pumpDuty,
                    $"Invalid iCUE cooler reading discarded. Raw row: {row.RawLine}"
                );
            }

            return new CoolerReadResult(
                true,
                updatedAt,
                sourceLog.FullName,
                liquidTemp,
                fan1Rpm,
                fan1Duty,
                fan2Rpm,
                fan2Duty,
                pumpRpm,
                pumpDuty,
                null
            );
        }
        catch (Exception ex)
        {
            return new CoolerReadResult(
                false,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                ex.Message
            );
        }
    }

    private static IcueCsvRow? ReadLatestIcueRow(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete
        );
        using var reader = new StreamReader(stream);

        var headerLine = reader.ReadLine();

        if (string.IsNullOrWhiteSpace(headerLine))
        {
            return null;
        }

        var headers = SplitCsvLine(headerLine);
        string? lastDataLine = null;

        while (!reader.EndOfStream)
        {
            var line = reader.ReadLine();

            if (!string.IsNullOrWhiteSpace(line))
            {
                lastDataLine = line;
            }
        }

        if (lastDataLine == null)
        {
            return null;
        }

        var values = SplitCsvLine(lastDataLine);

        if (values.Count != headers.Count || values.Count < 2)
        {
            return null;
        }

        var columns = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 1; i < headers.Count; i++)
        {
            columns[headers[i]] = values[i];
        }

        return new IcueCsvRow(values[0], columns, lastDataLine);
    }

    private static List<string> SplitCsvLine(string line)
    {
        var result = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];

            if (ch == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                    continue;
                }

                inQuotes = !inQuotes;
                continue;
            }

            if (ch == ',' && !inQuotes)
            {
                result.Add(current.ToString().Trim());
                current.Clear();
                continue;
            }

            current.Append(ch);
        }

        result.Add(current.ToString().Trim());
        return result;
    }

    private static float? ParseSensorFloat(Dictionary<string, string> columns, string name)
    {
        if (!columns.TryGetValue(name, out var raw))
        {
            return null;
        }

        var match = Regex.Match(raw, @"-?[0-9]+(?:\.[0-9]+)?");

        if (!match.Success)
        {
            return null;
        }

        return float.Parse(match.Value, CultureInfo.InvariantCulture);
    }

    private static float? EstimateDuty(float? rpm, float maxRpm)
    {
        if (rpm == null || maxRpm <= 0)
        {
            return null;
        }

        var duty = rpm.Value / maxRpm * 100;
        return (float)Math.Round(Math.Clamp(duty, 0, 100), 1);
    }

    private static DateTimeOffset? ParseIcueTimestamp(string raw)
    {
        var cleaned = Regex.Replace(raw.Trim(), @"\s+(AM|PM)$", "", RegexOptions.IgnoreCase);
        var formats = new[]
        {
            "d/M/yyyy H:mm:ss",
            "dd/M/yyyy H:mm:ss",
            "d/MM/yyyy H:mm:ss",
            "dd/MM/yyyy H:mm:ss"
        };

        if (DateTime.TryParseExact(
            cleaned,
            formats,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeLocal,
            out var parsed
        ))
        {
            return new DateTimeOffset(parsed);
        }

        if (DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out parsed))
        {
            return new DateTimeOffset(parsed);
        }

        return null;
    }

    private record IcueCsvRow(
        string Timestamp,
        Dictionary<string, string> Columns,
        string RawLine
    );
}
