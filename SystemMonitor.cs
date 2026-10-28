using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;

namespace CountdownSaver;

public sealed record SystemSnapshot(
    double? CpuLoad,
    double? CpuTemp,
    double RamUsedGb,
    double RamTotalGb,
    double? GpuLoad,
    double? GpuTemp,
    double? VramUsedGb,
    double? VramTotalGb);

// Раз в секунду собирает загрузку и температуры в фоновом потоке.
//  • ЦП и ОЗУ — напрямую через Windows API.
//  • Видеокарта NVIDIA — через драйвер (nvml.dll): загрузка, температура, видеопамять.
//  • Любая другая видеокарта — загрузка из счётчиков Windows «GPU Engine».
//  • Температура процессора (и не-NVIDIA видеокарт) — Windows не отдаёт её без драйвера
//    с правами администратора, поэтому берём у LibreHardwareMonitor через WMI, если он запущен.
public sealed class SystemMonitor
{
    volatile SystemSnapshot? _latest;
    public SystemSnapshot? Latest => _latest;

    public void Start() => new Thread(Run) { IsBackground = true, Name = "SystemMonitor" }.Start();

    void Run()
    {
        var nvidia = Nvml.TryOpen();
        Log.Write(nvidia != null ? "System: видеокарта NVIDIA найдена" : "System: NVIDIA не найдена, загрузка ГП из счётчиков Windows");

        ulong lastIdle = 0, lastTotal = 0;
        int tick = 0;
        (double? Cpu, double? Gpu, double? GpuLoad) sensors = default;

        while (true)
        {
            try
            {
                // ЦП: доля времени не в простое между двумя замерами.
                double? cpu = null;
                if (GetSystemTimes(out var idleFt, out var kernelFt, out var userFt))
                {
                    ulong idle = idleFt.Value, total = kernelFt.Value + userFt.Value; // kernel включает idle
                    if (lastTotal != 0 && total > lastTotal)
                        cpu = Math.Clamp(100.0 * (1 - (double)(idle - lastIdle) / (total - lastTotal)), 0, 100);
                    lastIdle = idle;
                    lastTotal = total;
                }

                var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
                GlobalMemoryStatusEx(ref mem);
                const double gb = 1024.0 * 1024 * 1024;

                // Сенсоры LibreHardwareMonitor опрашиваем реже — WMI не самый быстрый.
                if (tick % 2 == 0)
                    sensors = HardwareMonitorWmi.Read();
                tick++;

                double? gpuLoad = null, gpuTemp = null, vramUsed = null, vramTotal = null;
                if (nvidia != null && nvidia.Read(out var nv))
                {
                    gpuLoad = nv.Load;
                    gpuTemp = nv.Temp;
                    vramUsed = nv.MemUsed / gb;
                    vramTotal = nv.MemTotal / gb;
                }
                gpuLoad ??= sensors.GpuLoad ?? GpuCounters.Read();
                gpuTemp ??= sensors.Gpu;

                _latest = new SystemSnapshot(
                    cpu,
                    sensors.Cpu,
                    (mem.ullTotalPhys - mem.ullAvailPhys) / gb,
                    mem.ullTotalPhys / gb,
                    gpuLoad,
                    gpuTemp,
                    vramUsed,
                    vramTotal);
            }
            catch (Exception ex)
            {
                Log.Error("System", ex);
            }
            Thread.Sleep(1000);
        }
    }

    // ---------- Загрузка ГП из счётчиков Windows (любой производитель) ----------

    static class GpuCounters
    {
        static readonly Regex Adapter = new(@"luid_0x[0-9a-f]+_0x[0-9a-f]+_phys_\d+", RegexOptions.IgnoreCase);
        static List<(string Adapter, PerformanceCounter Counter)>? _counters;
        static DateTime _builtAt;
        static bool _unavailable;

        public static double? Read()
        {
            if (_unavailable)
                return null;
            try
            {
                // Экземпляры появляются и исчезают вместе с процессами — периодически пересобираем список.
                if (_counters == null || DateTime.Now - _builtAt > TimeSpan.FromSeconds(15))
                {
                    _counters?.ForEach(c => c.Counter.Dispose());
                    _counters = new PerformanceCounterCategory("GPU Engine").GetInstanceNames()
                        .Where(n => n.Contains("engtype_3D", StringComparison.OrdinalIgnoreCase))
                        .Select(n => (Adapter.Match(n).Value, new PerformanceCounter("GPU Engine", "Utilization Percentage", n, true)))
                        .ToList();
                    _builtAt = DateTime.Now;
                    _counters.ForEach(c => c.Counter.NextValue()); // первый замер всегда 0
                    return null;
                }

                // Считаем по каждому адаптеру отдельно и берём самый загруженный (дискретную, а не встройку).
                var perAdapter = new Dictionary<string, double>();
                foreach (var (adapter, counter) in _counters)
                {
                    try
                    {
                        perAdapter[adapter] = perAdapter.GetValueOrDefault(adapter) + counter.NextValue();
                    }
                    catch
                    {
                        // Процесс завершился — счётчик пропал.
                    }
                }
                return perAdapter.Count == 0 ? 0 : Math.Min(100, perAdapter.Values.Max());
            }
            catch (Exception ex)
            {
                Log.Error("System: счётчики GPU Engine недоступны", ex);
                _unavailable = true;
                return null;
            }
        }
    }

    // ---------- Температуры от LibreHardwareMonitor / OpenHardwareMonitor ----------

    static class HardwareMonitorWmi
    {
        static readonly string[] Namespaces = { @"root\LibreHardwareMonitor", @"root\OpenHardwareMonitor" };
        static readonly string[] CpuNames = { "CPU Package", "Core (Tctl/Tdie)", "Core (Tctl)", "Tctl/Tdie", "Core Average", "CPU Core" };
        static DateTime _retryAt;

        public static (double? Cpu, double? Gpu, double? GpuLoad) Read()
        {
            if (DateTime.Now < _retryAt)
                return default;

            foreach (var ns in Namespaces)
            {
                try
                {
                    using var searcher = new ManagementObjectSearcher(ns,
                        "SELECT Name, SensorType, Value, Parent FROM Sensor WHERE SensorType='Temperature' OR SensorType='Load'");
                    var sensors = new List<(string Name, string Type, double Value, string Parent)>();
                    foreach (ManagementBaseObject o in searcher.Get())
                    {
                        using (o)
                        {
                            sensors.Add(((string)o["Name"], (string)o["SensorType"],
                                Convert.ToDouble(o["Value"]), ((string)o["Parent"]).ToLowerInvariant()));
                        }
                    }
                    if (sensors.Count == 0)
                        continue;

                    var cpuTemps = sensors.Where(s => s.Type == "Temperature" && s.Parent.Contains("cpu") && s.Value > 0).ToList();
                    var gpuTemps = sensors.Where(s => s.Type == "Temperature" && s.Parent.Contains("gpu") && s.Value > 0).ToList();
                    var gpuLoads = sensors.Where(s => s.Type == "Load" && s.Parent.Contains("gpu")
                                                      && (s.Name == "GPU Core" || s.Name == "D3D 3D")).ToList();

                    double? cpu = Pick(cpuTemps, CpuNames);
                    double? gpu = Pick(gpuTemps, "GPU Core");
                    double? gpuLoad = gpuLoads.Count > 0 ? gpuLoads.Max(s => s.Value) : null;
                    return (cpu, gpu, gpuLoad);
                }
                catch (ManagementException)
                {
                    // Программа не запущена — пространства имён нет.
                }
                catch (Exception ex)
                {
                    Log.Error("System: WMI " + ns, ex);
                }
            }

            // Не нашли — не дёргаем WMI каждую секунду, проверим снова через полминуты.
            _retryAt = DateTime.Now.AddSeconds(30);
            return default;
        }

        // Первый датчик из списка предпочтительных имён, иначе самый горячий.
        static double? Pick(List<(string Name, string Type, double Value, string Parent)> sensors, params string[] preferred)
        {
            foreach (var name in preferred)
            {
                foreach (var s in sensors)
                {
                    if (s.Name == name)
                        return s.Value;
                }
            }
            return sensors.Count > 0 ? sensors.Max(s => s.Value) : null;
        }
    }

    // ---------- NVIDIA (nvml.dll ставится вместе с драйвером) ----------

    sealed class Nvml
    {
        readonly IntPtr _device;

        Nvml(IntPtr device) => _device = device;

        public static Nvml? TryOpen()
        {
            try
            {
                if (nvmlInit_v2() != 0)
                    return null;
                if (nvmlDeviceGetHandleByIndex_v2(0, out var device) != 0)
                    return null;
                return new Nvml(device);
            }
            catch
            {
                // Нет nvml.dll — значит, не NVIDIA.
                return null;
            }
        }

        public bool Read(out (double? Load, double? Temp, double MemUsed, double MemTotal) value)
        {
            value = default;
            try
            {
                double? load = nvmlDeviceGetUtilizationRates(_device, out var util) == 0 ? util.Gpu : null;
                double? temp = nvmlDeviceGetTemperature(_device, 0, out var t) == 0 ? t : null;
                var mem = default(NvmlMemory);
                bool memOk = nvmlDeviceGetMemoryInfo(_device, ref mem) == 0;
                value = (load, temp, memOk ? mem.Used : 0, memOk ? mem.Total : 0);
                return load != null || temp != null;
            }
            catch
            {
                return false;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        struct NvmlUtilization { public uint Gpu; public uint Memory; }

        [StructLayout(LayoutKind.Sequential)]
        struct NvmlMemory { public ulong Total; public ulong Free; public ulong Used; }

        [DllImport("nvml.dll")] static extern int nvmlInit_v2();
        [DllImport("nvml.dll")] static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);
        [DllImport("nvml.dll")] static extern int nvmlDeviceGetUtilizationRates(IntPtr device, out NvmlUtilization utilization);
        [DllImport("nvml.dll")] static extern int nvmlDeviceGetTemperature(IntPtr device, int sensor, out uint temp);
        [DllImport("nvml.dll")] static extern int nvmlDeviceGetMemoryInfo(IntPtr device, ref NvmlMemory memory);
    }

    // ---------- WinAPI ----------

    [StructLayout(LayoutKind.Sequential)]
    struct FILETIME
    {
        public uint Low;
        public uint High;
        public ulong Value => ((ulong)High << 32) | Low;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll")] static extern bool GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user);
    [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);
}
