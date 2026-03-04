using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Unity.MemoryProfiler.Editor.UI
{
    internal static class SnapshotComparisonExporter
    {
        [MenuItem("MemoryProfiler2/Export Snapshot Comparison (CSV)")]
        public static void ExportSnapshotComparison()
        {
            // 尝试查找已经打开的 Memory Profiler 窗口，而不是创建新的
            var windows = Resources.FindObjectsOfTypeAll<MemoryProfilerWindow>();
            MemoryProfilerWindow window = windows.Length > 0 ? windows[0] : null;
            
            if (window == null || window.UIState == null)
            {
                EditorUtility.DisplayDialog("错误", "请先打开 Memory Profiler 窗口", "确定");
                return;
            }

            var diffMode = window.UIState.diffMode;
            if (diffMode == null)
            {
                EditorUtility.DisplayDialog("错误", "请先进入对比模式（Compare Mode）", "确定");
                return;
            }

            var snapshotA = (diffMode.modeA as UIState.SnapshotMode)?.snapshot;
            var snapshotB = (diffMode.modeB as UIState.SnapshotMode)?.snapshot;

            if (snapshotA == null || snapshotB == null)
            {
                EditorUtility.DisplayDialog("错误", "无法获取快照数据", "确定");
                return;
            }

            // 获取快照名称
            string snapshotAName = GetSnapshotName(snapshotA);
            string snapshotBName = GetSnapshotName(snapshotB);

            // 让用户选择保存路径
            string defaultFileName = $"SnapshotComparison_{snapshotAName}_VS_{snapshotBName}.csv";
            string path = EditorUtility.SaveFilePanel(
                "导出快照对比数据",
                "",
                defaultFileName,
                "csv"
            );

            if (string.IsNullOrEmpty(path))
            {
                return; // 用户取消了
            }

            try
            {
                ExportComparisonToCSV(path, snapshotA, snapshotB, snapshotAName, snapshotBName);
                EditorUtility.DisplayDialog("成功", $"对比数据已导出到:\n{path}", "确定");
                EditorUtility.RevealInFinder(path);
            }
            catch (Exception ex)
            {
                EditorUtility.DisplayDialog("错误", $"导出失败:\n{ex.Message}", "确定");
                Debug.LogError($"导出快照对比数据失败: {ex}");
            }
        }

        static string GetSnapshotName(CachedSnapshot snapshot)
        {
            if (snapshot.MetaData == null)
                return "Unknown";

            // 尝试从元数据中获取名称
            var sessionName = snapshot.MetaData.ProductName ?? "Unknown";
            var timestamp = snapshot.TimeStamp;
            return $"{sessionName}_{timestamp:MMdd_HHmmss}";
        }

        static void ExportComparisonToCSV(string path, CachedSnapshot snapshotA, CachedSnapshot snapshotB, string nameA, string nameB)
        {
            using (var writer = new StreamWriter(path, false, Encoding.UTF8))
            {
                // 写入标题
                writer.WriteLine($"快照对比: {nameA} VS {nameB}");
                writer.WriteLine();

                // 一、Memory Usage Overview
                WriteMemoryUsageOverview(writer, snapshotA, snapshotB, nameA, nameB);
                writer.WriteLine();

                // 二、Unity Objects - Allocated Size (前10)
                WriteUnityObjectsBySize(writer, snapshotA, snapshotB, nameA, nameB);
                writer.WriteLine();

                // 三、Unity Objects - Count (前10)
                WriteUnityObjectsByCount(writer, snapshotA, snapshotB, nameA, nameB);
                writer.WriteLine();

                // 四、Managed Objects - Allocated Size (前10)
                WriteManagedObjectsBySize(writer, snapshotA, snapshotB, nameA, nameB);
                writer.WriteLine();

                // 五、Managed Objects - Count (前10)
                WriteManagedObjectsByCount(writer, snapshotA, snapshotB, nameA, nameB);
            }
        }

        static void WriteMemoryUsageOverview(StreamWriter writer, CachedSnapshot snapshotA, CachedSnapshot snapshotB, string nameA, string nameB)
        {
            writer.WriteLine("一、Memory Usage Overview");
            writer.WriteLine($"分类,{nameA},{nameB},增长百分比");

            var statsA = snapshotA.MetaData.TargetMemoryStats;
            var statsB = snapshotB.MetaData.TargetMemoryStats;

            if (statsA.HasValue && statsB.HasValue)
            {
                WriteMemoryStat(writer, "Managed Heap in use", 
                    statsA.Value.GcHeapUsedMemory, statsB.Value.GcHeapUsedMemory, nameA, nameB);
                WriteMemoryStat(writer, "Managed Heap in Reserved", 
                    statsA.Value.GcHeapReservedMemory, statsB.Value.GcHeapReservedMemory, nameA, nameB);
                WriteMemoryStat(writer, "Graphics&Graphics Driver", 
                    statsA.Value.GraphicsUsedMemory, statsB.Value.GraphicsUsedMemory, nameA, nameB);
                WriteMemoryStat(writer, "Audio", 
                    statsA.Value.AudioUsedMemory, statsB.Value.AudioUsedMemory, nameA, nameB);

                // Other Native Memory in use
                var otherNativeUsedA = statsA.Value.TotalUsedMemory - statsA.Value.GcHeapUsedMemory - 
                                       statsA.Value.GraphicsUsedMemory - statsA.Value.AudioUsedMemory - 
                                       statsA.Value.ProfilerUsedMemory;
                var otherNativeUsedB = statsB.Value.TotalUsedMemory - statsB.Value.GcHeapUsedMemory - 
                                       statsB.Value.GraphicsUsedMemory - statsB.Value.AudioUsedMemory - 
                                       statsB.Value.ProfilerUsedMemory;
                WriteMemoryStat(writer, "Other Native Memory in use", otherNativeUsedA, otherNativeUsedB, nameA, nameB);

                // Other Native Memory in Reserved
                var otherNativeReservedA = statsA.Value.TotalReservedMemory - statsA.Value.GcHeapReservedMemory - 
                                           statsA.Value.ProfilerReservedMemory;
                var otherNativeReservedB = statsB.Value.TotalReservedMemory - statsB.Value.GcHeapReservedMemory - 
                                           statsB.Value.ProfilerReservedMemory;
                WriteMemoryStat(writer, "Other Native Memory in Reserved", otherNativeReservedA, otherNativeReservedB, nameA, nameB);

                WriteMemoryStat(writer, "Profiler in use", 
                    statsA.Value.ProfilerUsedMemory, statsB.Value.ProfilerUsedMemory, nameA, nameB);
                WriteMemoryStat(writer, "Profiler in reserved", 
                    statsA.Value.ProfilerReservedMemory, statsB.Value.ProfilerReservedMemory, nameA, nameB);

                // Executable&DLLs - 通常为0或未跟踪
                WriteMemoryStat(writer, "Executable&DLLs", 0, 0, nameA, nameB);

                // Untracked Memory
                var untrackedA = statsA.Value.TotalVirtualMemory - statsA.Value.TotalReservedMemory;
                var untrackedB = statsB.Value.TotalVirtualMemory - statsB.Value.TotalReservedMemory;
                WriteMemoryStat(writer, "Untracked Memory", untrackedA, untrackedB, nameA, nameB);

                // Managed Memory 详细信息
                var managedObjectsSizeA = GetTotalManagedObjectsSize(snapshotA);
                var managedObjectsSizeB = GetTotalManagedObjectsSize(snapshotB);
                WriteMemoryStat(writer, "Managed Memory-Objects", managedObjectsSizeA, managedObjectsSizeB, nameA, nameB);

                // Empty Active Heap Space = Reserved - Used
                var emptyHeapA = statsA.Value.GcHeapReservedMemory - statsA.Value.GcHeapUsedMemory;
                var emptyHeapB = statsB.Value.GcHeapReservedMemory - statsB.Value.GcHeapUsedMemory;
                
                // Fragmented Heap Space = Used - Objects
                var fragmentedA = statsA.Value.GcHeapUsedMemory > managedObjectsSizeA ? 
                                  statsA.Value.GcHeapUsedMemory - managedObjectsSizeA : 0;
                var fragmentedB = statsB.Value.GcHeapUsedMemory > managedObjectsSizeB ? 
                                  statsB.Value.GcHeapUsedMemory - managedObjectsSizeB : 0;
                
                WriteMemoryStat(writer, "Managed Memory-Empty Active Heap Space", emptyHeapA, emptyHeapB, nameA, nameB);
                WriteMemoryStat(writer, "Managed Memory-Fragmented Heap Space", fragmentedA, fragmentedB, nameA, nameB);
            }
        }

        static ulong GetTotalManagedObjectsSize(CachedSnapshot snapshot)
        {
            ulong totalSize = 0;
            var managedObjects = snapshot.CrawledData.ManagedObjects;
            
            for (int i = 0; i < managedObjects.Count; i++)
            {
                totalSize += (ulong)managedObjects[i].Size;
            }
            
            return totalSize;
        }

        // CSV 转义函数：处理字段中的特殊字符（逗号、引号、换行符）
        static string EscapeCSVField(string field)
        {
            if (string.IsNullOrEmpty(field))
                return field;
            
            // 如果字段包含逗号、引号或换行符，需要用双引号括起来
            if (field.Contains(",") || field.Contains("\"") || field.Contains("\n") || field.Contains("\r"))
            {
                // 将字段中的双引号替换为两个双引号（CSV 标准转义方式）
                return $"\"{field.Replace("\"", "\"\"")}\"";
            }
            
            return field;
        }

        static void WriteMemoryStat(StreamWriter writer, string name, ulong valueA, ulong valueB, string nameA, string nameB)
        {
            double mbA = valueA / (1024.0 * 1024.0);
            double mbB = valueB / (1024.0 * 1024.0);
            double growthPercent = valueA > 0 ? ((double)valueB - valueA) / valueA * 100.0 : 0;
            
            writer.WriteLine($"{EscapeCSVField(name)},{mbA:F2}MB,{mbB:F2}MB,{growthPercent:F2}%");
        }

        static void WriteUnityObjectsBySize(StreamWriter writer, CachedSnapshot snapshotA, CachedSnapshot snapshotB, string nameA, string nameB)
        {
            writer.WriteLine("二、Unity Objects - Allocated Size (MB) 前10");
            writer.WriteLine($"分类,{nameA},{nameB},增长百分比");

            var typesA = GetNativeObjectTypeStats(snapshotA);
            var typesB = GetNativeObjectTypeStats(snapshotB);

            var allTypes = typesA.Keys.Union(typesB.Keys).ToList();
            var topTypes = allTypes
                .OrderByDescending(t => Math.Max(
                    typesA.ContainsKey(t) ? typesA[t].totalSize : 0,
                    typesB.ContainsKey(t) ? typesB[t].totalSize : 0))
                .Take(10);

            foreach (var typeName in topTypes)
            {
                ulong sizeA = typesA.ContainsKey(typeName) ? typesA[typeName].totalSize : 0;
                ulong sizeB = typesB.ContainsKey(typeName) ? typesB[typeName].totalSize : 0;
                
                double mbA = sizeA / (1024.0 * 1024.0);
                double mbB = sizeB / (1024.0 * 1024.0);
                double growthPercent = sizeA > 0 ? ((double)sizeB - sizeA) / sizeA * 100.0 : (sizeB > 0 ? 100.0 : 0.0);
                
                writer.WriteLine($"{EscapeCSVField(typeName)},{mbA:F3},{mbB:F3},{growthPercent:F2}%");
            }
        }

        static void WriteUnityObjectsByCount(StreamWriter writer, CachedSnapshot snapshotA, CachedSnapshot snapshotB, string nameA, string nameB)
        {
            writer.WriteLine("三、Unity Objects - Count 前10");
            writer.WriteLine($"分类,{nameA},{nameB},增长百分比");

            var typesA = GetNativeObjectTypeStats(snapshotA);
            var typesB = GetNativeObjectTypeStats(snapshotB);

            var allTypes = typesA.Keys.Union(typesB.Keys).ToList();
            var topTypes = allTypes
                .OrderByDescending(t => Math.Max(
                    typesA.ContainsKey(t) ? typesA[t].count : 0,
                    typesB.ContainsKey(t) ? typesB[t].count : 0))
                .Take(10);

            foreach (var typeName in topTypes)
            {
                int countA = typesA.ContainsKey(typeName) ? typesA[typeName].count : 0;
                int countB = typesB.ContainsKey(typeName) ? typesB[typeName].count : 0;
                
                double growthPercent = countA > 0 ? ((double)countB - countA) / countA * 100.0 : (countB > 0 ? 100.0 : 0.0);
                
                writer.WriteLine($"{EscapeCSVField(typeName)},{countA},{countB},{growthPercent:F2}%");
            }
        }

        static void WriteManagedObjectsBySize(StreamWriter writer, CachedSnapshot snapshotA, CachedSnapshot snapshotB, string nameA, string nameB)
        {
            writer.WriteLine("四、Managed Objects - Allocated Size (MB) 前10");
            writer.WriteLine($"分类,{nameA},{nameB},增长百分比");

            var typesA = GetManagedObjectTypeStats(snapshotA);
            var typesB = GetManagedObjectTypeStats(snapshotB);

            var allTypes = typesA.Keys.Union(typesB.Keys).ToList();
            var topTypes = allTypes
                .OrderByDescending(t => Math.Max(
                    typesA.ContainsKey(t) ? typesA[t].totalSize : 0,
                    typesB.ContainsKey(t) ? typesB[t].totalSize : 0))
                .Take(10);

            foreach (var typeName in topTypes)
            {
                ulong sizeA = typesA.ContainsKey(typeName) ? typesA[typeName].totalSize : 0;
                ulong sizeB = typesB.ContainsKey(typeName) ? typesB[typeName].totalSize : 0;
                
                double mbA = sizeA / (1024.0 * 1024.0);
                double mbB = sizeB / (1024.0 * 1024.0);
                double growthPercent = sizeA > 0 ? ((double)sizeB - sizeA) / sizeA * 100.0 : (sizeB > 0 ? 100.0 : 0.0);
                
                writer.WriteLine($"{EscapeCSVField(typeName)},{mbA:F3},{mbB:F3},{growthPercent:F2}%");
            }
        }

        static void WriteManagedObjectsByCount(StreamWriter writer, CachedSnapshot snapshotA, CachedSnapshot snapshotB, string nameA, string nameB)
        {
            writer.WriteLine("五、Managed Objects - Count 前10");
            writer.WriteLine($"分类,{nameA},{nameB},增长百分比");

            var typesA = GetManagedObjectTypeStats(snapshotA);
            var typesB = GetManagedObjectTypeStats(snapshotB);

            var allTypes = typesA.Keys.Union(typesB.Keys).ToList();
            var topTypes = allTypes
                .OrderByDescending(t => Math.Max(
                    typesA.ContainsKey(t) ? typesA[t].count : 0,
                    typesB.ContainsKey(t) ? typesB[t].count : 0))
                .Take(10);

            foreach (var typeName in topTypes)
            {
                int countA = typesA.ContainsKey(typeName) ? typesA[typeName].count : 0;
                int countB = typesB.ContainsKey(typeName) ? typesB[typeName].count : 0;
                
                double growthPercent = countA > 0 ? ((double)countB - countA) / countA * 100.0 : (countB > 0 ? 100.0 : 0.0);
                
                writer.WriteLine($"{EscapeCSVField(typeName)},{countA},{countB},{growthPercent:F2}%");
            }
        }

        static Dictionary<string, (ulong totalSize, int count)> GetNativeObjectTypeStats(CachedSnapshot snapshot)
        {
            var stats = new Dictionary<string, (ulong totalSize, int count)>();
            
            var nativeObjects = snapshot.NativeObjects;
            var nativeTypes = snapshot.NativeTypes;

            for (int i = 0; i < nativeObjects.Count; i++)
            {
                var typeIndex = nativeObjects.NativeTypeArrayIndex[i];
                if (typeIndex < 0 || typeIndex >= nativeTypes.Count)
                    continue;

                var typeName = nativeTypes.TypeName[typeIndex];
                var size = nativeObjects.Size[i];

                if (stats.ContainsKey(typeName))
                {
                    var current = stats[typeName];
                    stats[typeName] = (current.totalSize + size, current.count + 1);
                }
                else
                {
                    stats[typeName] = (size, 1);
                }
            }

            return stats;
        }

        static Dictionary<string, (ulong totalSize, int count)> GetManagedObjectTypeStats(CachedSnapshot snapshot)
        {
            var stats = new Dictionary<string, (ulong totalSize, int count)>();
            
            var managedObjects = snapshot.CrawledData.ManagedObjects;
            var typeDescriptions = snapshot.TypeDescriptions;

            for (int i = 0; i < managedObjects.Count; i++)
            {
                var obj = managedObjects[i];
                var typeIndex = obj.ITypeDescription;
                
                if (typeIndex < 0 || typeIndex >= typeDescriptions.Count)
                    continue;

                var typeName = typeDescriptions.TypeDescriptionName[typeIndex];
                var size = (ulong)obj.Size;

                if (stats.ContainsKey(typeName))
                {
                    var current = stats[typeName];
                    stats[typeName] = (current.totalSize + size, current.count + 1);
                }
                else
                {
                    stats[typeName] = (size, 1);
                }
            }

            return stats;
        }
    }
}
