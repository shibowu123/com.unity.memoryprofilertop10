using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Unity.MemoryProfiler.Editor.Format.QueriedSnapshot;
using UnityEditor;
using UnityEngine;

namespace Unity.MemoryProfiler.Editor.UI
{
    internal static class BatchMemoryCompare
    {
        static readonly string[] NativeTypesOfInterest = new string[]
        {
            "Texture2D", "Mesh", "Shader", "ParticleSystem", "Sprite",
            "AssetBundle", "MonoBehaviour", "Transform", "GameObject",
            "RenderTexture", "AnimationClip", "Material",
            "ParticleSystemRenderer", "MonoScript", "MeshRenderer", "Font"
        };

        static readonly string[] ManagedTypesOfInterest = new string[]
        {
            "System.String", "UnityEngine.UIVertex[]", "LTDescrImpl"
        };

        [MenuItem("MemoryProfiler2/Batch Compare (CLI)")]
        public static void RunFromMenu()
        {
            string oldSnap = EditorUtility.OpenFilePanel("选择旧版本快照", "MemoryCaptures", "snap");
            if (string.IsNullOrEmpty(oldSnap)) return;

            string newSnap = EditorUtility.OpenFilePanel("选择新版本快照", "MemoryCaptures", "snap");
            if (string.IsNullOrEmpty(newSnap)) return;

            string outputDir = EditorUtility.SaveFolderPanel("选择输出目录", "", "memory_compare");
            if (string.IsNullOrEmpty(outputDir)) return;

            Execute(oldSnap, newSnap, outputDir, "旧版本", "新版本");
            EditorUtility.DisplayDialog("完成", $"对比数据已导出到:\n{outputDir}", "确定");
        }

        public static void Run()
        {
            var args = Environment.GetCommandLineArgs();
            string oldSnap = GetArg(args, "-oldSnap");
            string newSnap = GetArg(args, "-newSnap");
            string outputDir = GetArg(args, "-outputDir");
            string oldLabel = GetArg(args, "-oldLabel") ?? "旧版本";
            string newLabel = GetArg(args, "-newLabel") ?? "新版本";

            if (string.IsNullOrEmpty(oldSnap) || string.IsNullOrEmpty(newSnap) || string.IsNullOrEmpty(outputDir))
            {
                Debug.LogError("[BatchMemoryCompare] 缺少参数。需要: -oldSnap, -newSnap, -outputDir");
                EditorApplication.Exit(1);
                return;
            }

            if (!File.Exists(oldSnap))
            {
                Debug.LogError($"[BatchMemoryCompare] 旧版本快照不存在: {oldSnap}");
                EditorApplication.Exit(1);
                return;
            }

            if (!File.Exists(newSnap))
            {
                Debug.LogError($"[BatchMemoryCompare] 新版本快照不存在: {newSnap}");
                EditorApplication.Exit(1);
                return;
            }

            try
            {
                Execute(oldSnap, newSnap, outputDir, oldLabel, newLabel);
                Debug.Log($"[BatchMemoryCompare] 完成。输出目录: {outputDir}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[BatchMemoryCompare] 执行失败: {ex}");
                EditorApplication.Exit(1);
            }
        }

        static void Execute(string oldSnapPath, string newSnapPath, string outputDir, string oldLabel, string newLabel)
        {
            Directory.CreateDirectory(outputDir);

            Debug.Log($"[BatchMemoryCompare] 加载旧版本快照: {oldSnapPath}");
            var snapshotA = LoadSnapshot(oldSnapPath);

            Debug.Log($"[BatchMemoryCompare] 加载新版本快照: {newSnapPath}");
            var snapshotB = LoadSnapshot(newSnapPath);

            // 1. 总览对比 CSV
            string summaryPath = Path.Combine(outputDir, "summary.csv");
            Debug.Log("[BatchMemoryCompare] 导出总览对比...");
            ExportSummaryCSV(summaryPath, snapshotA, snapshotB, oldLabel, newLabel);

            // 2. 按 Type 导出单版本明细
            string oldDir = Path.Combine(outputDir, oldLabel);
            string newDir = Path.Combine(outputDir, newLabel);
            Directory.CreateDirectory(oldDir);
            Directory.CreateDirectory(newDir);

            Debug.Log("[BatchMemoryCompare] 导出 Native 类型明细...");
            ExportNativeTypeDetails(snapshotA, oldDir);
            ExportNativeTypeDetails(snapshotB, newDir);

            Debug.Log("[BatchMemoryCompare] 导出 Managed 类型明细...");
            ExportManagedTypeDetails(snapshotA, oldDir);
            ExportManagedTypeDetails(snapshotB, newDir);

            // 3. 按 Type 导出版本对比
            string diffDir = Path.Combine(outputDir, "diff");
            Directory.CreateDirectory(diffDir);

            Debug.Log("[BatchMemoryCompare] 导出 Native 类型对比...");
            ExportNativeTypeDiffs(snapshotA, snapshotB, diffDir, oldLabel, newLabel);

            Debug.Log("[BatchMemoryCompare] 导出 Managed 类型对比...");
            ExportManagedTypeDiffs(snapshotA, snapshotB, diffDir, oldLabel, newLabel);

            Debug.Log("[BatchMemoryCompare] 全部导出完成");
        }

        static CachedSnapshot LoadSnapshot(string path)
        {
            var reader = new FileReader();
            var err = reader.Open(path);
            if (err != ReadError.Success)
            {
                throw new Exception($"无法打开快照文件: {path}, 错误: {err}");
            }

            var cachedSnapshot = new CachedSnapshot(reader);

            var crawling = Crawler.Crawl(cachedSnapshot);
            while (crawling.MoveNext()) { }

            return cachedSnapshot;
        }

        #region Summary CSV

        static void ExportSummaryCSV(string path, CachedSnapshot snapshotA, CachedSnapshot snapshotB, string nameA, string nameB)
        {
            using (var writer = new StreamWriter(path, false, Encoding.UTF8))
            {
                writer.WriteLine($"快照对比: {nameA} VS {nameB}");
                writer.WriteLine();

                WriteMemoryUsageOverview(writer, snapshotA, snapshotB, nameA, nameB);
                writer.WriteLine();
                WriteUnityObjectsBySize(writer, snapshotA, snapshotB, nameA, nameB);
                writer.WriteLine();
                WriteUnityObjectsByCount(writer, snapshotA, snapshotB, nameA, nameB);
                writer.WriteLine();
                WriteManagedObjectsBySize(writer, snapshotA, snapshotB, nameA, nameB);
                writer.WriteLine();
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
                WriteMemoryStat(writer, "Managed Heap in use", statsA.Value.GcHeapUsedMemory, statsB.Value.GcHeapUsedMemory);
                WriteMemoryStat(writer, "Managed Heap in Reserved", statsA.Value.GcHeapReservedMemory, statsB.Value.GcHeapReservedMemory);
                WriteMemoryStat(writer, "Graphics&Graphics Driver", statsA.Value.GraphicsUsedMemory, statsB.Value.GraphicsUsedMemory);
                WriteMemoryStat(writer, "Audio", statsA.Value.AudioUsedMemory, statsB.Value.AudioUsedMemory);

                var otherNativeUsedA = statsA.Value.TotalUsedMemory - statsA.Value.GcHeapUsedMemory -
                                       statsA.Value.GraphicsUsedMemory - statsA.Value.AudioUsedMemory -
                                       statsA.Value.ProfilerUsedMemory;
                var otherNativeUsedB = statsB.Value.TotalUsedMemory - statsB.Value.GcHeapUsedMemory -
                                       statsB.Value.GraphicsUsedMemory - statsB.Value.AudioUsedMemory -
                                       statsB.Value.ProfilerUsedMemory;
                WriteMemoryStat(writer, "Other Native Memory in use", otherNativeUsedA, otherNativeUsedB);

                var otherNativeReservedA = statsA.Value.TotalReservedMemory - statsA.Value.GcHeapReservedMemory -
                                           statsA.Value.ProfilerReservedMemory;
                var otherNativeReservedB = statsB.Value.TotalReservedMemory - statsB.Value.GcHeapReservedMemory -
                                           statsB.Value.ProfilerReservedMemory;
                WriteMemoryStat(writer, "Other Native Memory in Reserved", otherNativeReservedA, otherNativeReservedB);

                WriteMemoryStat(writer, "Profiler in use", statsA.Value.ProfilerUsedMemory, statsB.Value.ProfilerUsedMemory);
                WriteMemoryStat(writer, "Profiler in reserved", statsA.Value.ProfilerReservedMemory, statsB.Value.ProfilerReservedMemory);
                WriteMemoryStat(writer, "Executable&DLLs", 0, 0);

                var untrackedA = statsA.Value.TotalVirtualMemory - statsA.Value.TotalReservedMemory;
                var untrackedB = statsB.Value.TotalVirtualMemory - statsB.Value.TotalReservedMemory;
                WriteMemoryStat(writer, "Untracked Memory", untrackedA, untrackedB);

                var managedObjectsSizeA = GetTotalManagedObjectsSize(snapshotA);
                var managedObjectsSizeB = GetTotalManagedObjectsSize(snapshotB);
                WriteMemoryStat(writer, "Managed Memory-Objects", managedObjectsSizeA, managedObjectsSizeB);

                var emptyHeapA = statsA.Value.GcHeapReservedMemory - statsA.Value.GcHeapUsedMemory;
                var emptyHeapB = statsB.Value.GcHeapReservedMemory - statsB.Value.GcHeapUsedMemory;
                WriteMemoryStat(writer, "Managed Memory-Empty Active Heap Space", emptyHeapA, emptyHeapB);

                var fragmentedA = statsA.Value.GcHeapUsedMemory > managedObjectsSizeA ?
                                  statsA.Value.GcHeapUsedMemory - managedObjectsSizeA : 0;
                var fragmentedB = statsB.Value.GcHeapUsedMemory > managedObjectsSizeB ?
                                  statsB.Value.GcHeapUsedMemory - managedObjectsSizeB : 0;
                WriteMemoryStat(writer, "Managed Memory-Fragmented Heap Space", fragmentedA, fragmentedB);
            }
        }

        static ulong GetTotalManagedObjectsSize(CachedSnapshot snapshot)
        {
            ulong totalSize = 0;
            var managedObjects = snapshot.CrawledData.ManagedObjects;
            for (int i = 0; i < managedObjects.Count; i++)
                totalSize += (ulong)managedObjects[i].Size;
            return totalSize;
        }

        static void WriteMemoryStat(StreamWriter writer, string name, ulong valueA, ulong valueB)
        {
            double mbA = valueA / (1024.0 * 1024.0);
            double mbB = valueB / (1024.0 * 1024.0);
            double growth = valueA > 0 ? ((double)valueB - valueA) / valueA * 100.0 : 0;
            writer.WriteLine($"{Escape(name)},{mbA:F2}MB,{mbB:F2}MB,{growth:F2}%");
        }

        static void WriteUnityObjectsBySize(StreamWriter writer, CachedSnapshot snapshotA, CachedSnapshot snapshotB, string nameA, string nameB)
        {
            writer.WriteLine("二、Unity Objects - Allocated Size (MB) 前10");
            writer.WriteLine($"分类,{nameA},{nameB},增长百分比");
            WriteTypeStatsTop10(writer, GetNativeTypeStats(snapshotA), GetNativeTypeStats(snapshotB), true);
        }

        static void WriteUnityObjectsByCount(StreamWriter writer, CachedSnapshot snapshotA, CachedSnapshot snapshotB, string nameA, string nameB)
        {
            writer.WriteLine("三、Unity Objects - Count 前10");
            writer.WriteLine($"分类,{nameA},{nameB},增长百分比");
            WriteTypeStatsTop10(writer, GetNativeTypeStats(snapshotA), GetNativeTypeStats(snapshotB), false);
        }

        static void WriteManagedObjectsBySize(StreamWriter writer, CachedSnapshot snapshotA, CachedSnapshot snapshotB, string nameA, string nameB)
        {
            writer.WriteLine("四、Managed Objects - Allocated Size (MB) 前10");
            writer.WriteLine($"分类,{nameA},{nameB},增长百分比");
            WriteTypeStatsTop10(writer, GetManagedTypeStats(snapshotA), GetManagedTypeStats(snapshotB), true);
        }

        static void WriteManagedObjectsByCount(StreamWriter writer, CachedSnapshot snapshotA, CachedSnapshot snapshotB, string nameA, string nameB)
        {
            writer.WriteLine("五、Managed Objects - Count 前10");
            writer.WriteLine($"分类,{nameA},{nameB},增长百分比");
            WriteTypeStatsTop10(writer, GetManagedTypeStats(snapshotA), GetManagedTypeStats(snapshotB), false);
        }

        static void WriteTypeStatsTop10(StreamWriter writer,
            Dictionary<string, (ulong totalSize, int count)> statsA,
            Dictionary<string, (ulong totalSize, int count)> statsB,
            bool bySize)
        {
            var allTypes = statsA.Keys.Union(statsB.Keys).ToList();
            IEnumerable<string> topTypes;

            if (bySize)
            {
                topTypes = allTypes
                    .OrderByDescending(t => Math.Max(
                        statsA.ContainsKey(t) ? statsA[t].totalSize : 0,
                        statsB.ContainsKey(t) ? statsB[t].totalSize : 0))
                    .Take(10);
            }
            else
            {
                topTypes = allTypes
                    .OrderByDescending(t => Math.Max(
                        statsA.ContainsKey(t) ? statsA[t].count : 0,
                        statsB.ContainsKey(t) ? statsB[t].count : 0))
                    .Take(10);
            }

            foreach (var typeName in topTypes)
            {
                if (bySize)
                {
                    ulong sizeA = statsA.ContainsKey(typeName) ? statsA[typeName].totalSize : 0;
                    ulong sizeB = statsB.ContainsKey(typeName) ? statsB[typeName].totalSize : 0;
                    double mbA = sizeA / (1024.0 * 1024.0);
                    double mbB = sizeB / (1024.0 * 1024.0);
                    double growth = sizeA > 0 ? ((double)sizeB - sizeA) / sizeA * 100.0 : (sizeB > 0 ? 100.0 : 0.0);
                    writer.WriteLine($"{Escape(typeName)},{mbA:F3},{mbB:F3},{growth:F2}%");
                }
                else
                {
                    int countA = statsA.ContainsKey(typeName) ? statsA[typeName].count : 0;
                    int countB = statsB.ContainsKey(typeName) ? statsB[typeName].count : 0;
                    double growth = countA > 0 ? ((double)countB - countA) / countA * 100.0 : (countB > 0 ? 100.0 : 0.0);
                    writer.WriteLine($"{Escape(typeName)},{countA},{countB},{growth:F2}%");
                }
            }
        }

        #endregion

        #region Per-Type Detail Export

        static void ExportNativeTypeDetails(CachedSnapshot snapshot, string outputDir)
        {
            var nativeObjects = snapshot.NativeObjects;
            var nativeTypes = snapshot.NativeTypes;

            var typeGroups = new Dictionary<string, List<(string name, ulong size, int instanceId)>>();

            for (int i = 0; i < nativeObjects.Count; i++)
            {
                var typeIndex = nativeObjects.NativeTypeArrayIndex[i];
                if (typeIndex < 0 || typeIndex >= nativeTypes.Count) continue;

                var typeName = nativeTypes.TypeName[typeIndex];
                if (!NativeTypesOfInterest.Contains(typeName)) continue;

                var name = nativeObjects.ObjectName[i];
                var size = nativeObjects.Size[i];
                var instanceId = nativeObjects.InstanceId[i];

                if (!typeGroups.ContainsKey(typeName))
                    typeGroups[typeName] = new List<(string, ulong, int)>();

                typeGroups[typeName].Add((name, size, instanceId));
            }

            foreach (var kvp in typeGroups)
            {
                var typeName = kvp.Key;
                var objects = kvp.Value.OrderByDescending(o => o.size).ToList();
                var filePath = Path.Combine(outputDir, $"{typeName}.csv");

                using (var writer = new StreamWriter(filePath, false, Encoding.UTF8))
                {
                    writer.WriteLine("Name,Size(bytes),Size(MB),InstanceID");
                    foreach (var obj in objects)
                    {
                        writer.WriteLine($"{Escape(obj.name)},{obj.size},{obj.size / (1024.0 * 1024.0):F4},{obj.instanceId}");
                    }
                }
            }
        }

        static void ExportManagedTypeDetails(CachedSnapshot snapshot, string outputDir)
        {
            var managedObjects = snapshot.CrawledData.ManagedObjects;
            var typeDescriptions = snapshot.TypeDescriptions;

            var typeGroups = new Dictionary<string, List<(string typeName, long size, ulong address)>>();

            for (int i = 0; i < managedObjects.Count; i++)
            {
                var obj = managedObjects[i];
                var typeIndex = obj.ITypeDescription;
                if (typeIndex < 0 || typeIndex >= typeDescriptions.Count) continue;

                var typeName = typeDescriptions.TypeDescriptionName[typeIndex];
                if (!ManagedTypesOfInterest.Contains(typeName)) continue;

                if (!typeGroups.ContainsKey(typeName))
                    typeGroups[typeName] = new List<(string, long, ulong)>();

                typeGroups[typeName].Add((typeName, obj.Size, obj.PtrObject));
            }

            foreach (var kvp in typeGroups)
            {
                var typeName = kvp.Key;
                var objects = kvp.Value.OrderByDescending(o => o.size).ToList();
                var safeFileName = typeName.Replace("[]", "_Array").Replace("<", "_").Replace(">", "_").Replace(".", "_");
                var filePath = Path.Combine(outputDir, $"{safeFileName}.csv");

                using (var writer = new StreamWriter(filePath, false, Encoding.UTF8))
                {
                    writer.WriteLine("Type,Size(bytes),Size(KB),Address");
                    foreach (var obj in objects)
                    {
                        writer.WriteLine($"{Escape(obj.typeName)},{obj.size},{obj.size / 1024.0:F2},0x{obj.address:X16}");
                    }
                }
            }
        }

        #endregion

        #region Per-Type Diff Export

        static void ExportNativeTypeDiffs(CachedSnapshot snapshotA, CachedSnapshot snapshotB, string diffDir, string labelA, string labelB)
        {
            foreach (var targetType in NativeTypesOfInterest)
            {
                var objectsA = GetNativeObjectsOfType(snapshotA, targetType);
                var objectsB = GetNativeObjectsOfType(snapshotB, targetType);

                if (objectsA.Count == 0 && objectsB.Count == 0) continue;

                var diffPath = Path.Combine(diffDir, $"{targetType}_diff.csv");
                WriteDiffCSV(diffPath, objectsA, objectsB, labelA, labelB);
            }
        }

        static void ExportManagedTypeDiffs(CachedSnapshot snapshotA, CachedSnapshot snapshotB, string diffDir, string labelA, string labelB)
        {
            foreach (var targetType in ManagedTypesOfInterest)
            {
                var objectsA = GetManagedObjectsOfType(snapshotA, targetType);
                var objectsB = GetManagedObjectsOfType(snapshotB, targetType);

                if (objectsA.Count == 0 && objectsB.Count == 0) continue;

                var safeFileName = targetType.Replace("[]", "_Array").Replace("<", "_").Replace(">", "_").Replace(".", "_");
                var diffPath = Path.Combine(diffDir, $"{safeFileName}_diff.csv");
                WriteDiffCSV(diffPath, objectsA, objectsB, labelA, labelB);
            }
        }

        static Dictionary<string, ulong> GetNativeObjectsOfType(CachedSnapshot snapshot, string typeName)
        {
            var result = new Dictionary<string, ulong>();
            var nativeObjects = snapshot.NativeObjects;
            var nativeTypes = snapshot.NativeTypes;

            for (int i = 0; i < nativeObjects.Count; i++)
            {
                var typeIndex = nativeObjects.NativeTypeArrayIndex[i];
                if (typeIndex < 0 || typeIndex >= nativeTypes.Count) continue;

                if (nativeTypes.TypeName[typeIndex] != typeName) continue;

                var name = nativeObjects.ObjectName[i];
                var size = nativeObjects.Size[i];

                if (result.ContainsKey(name))
                    result[name] += size;
                else
                    result[name] = size;
            }

            return result;
        }

        static Dictionary<string, ulong> GetManagedObjectsOfType(CachedSnapshot snapshot, string typeName)
        {
            var result = new Dictionary<string, ulong>();
            var managedObjects = snapshot.CrawledData.ManagedObjects;
            var typeDescriptions = snapshot.TypeDescriptions;
            var counter = new Dictionary<string, int>();

            for (int i = 0; i < managedObjects.Count; i++)
            {
                var obj = managedObjects[i];
                var typeIndex = obj.ITypeDescription;
                if (typeIndex < 0 || typeIndex >= typeDescriptions.Count) continue;

                if (typeDescriptions.TypeDescriptionName[typeIndex] != typeName) continue;

                var key = $"0x{obj.PtrObject:X16}";
                result[key] = (ulong)obj.Size;
            }

            return result;
        }

        static void WriteDiffCSV(string path, Dictionary<string, ulong> objectsA, Dictionary<string, ulong> objectsB, string labelA, string labelB)
        {
            var allKeys = objectsA.Keys.Union(objectsB.Keys).ToList();

            var diffItems = new List<(string name, ulong sizeA, ulong sizeB, long delta, string status)>();

            foreach (var key in allKeys)
            {
                bool inA = objectsA.ContainsKey(key);
                bool inB = objectsB.ContainsKey(key);
                ulong sizeA = inA ? objectsA[key] : 0;
                ulong sizeB = inB ? objectsB[key] : 0;
                long delta = (long)sizeB - (long)sizeA;

                string status;
                if (!inA) status = "new";
                else if (!inB) status = "deleted";
                else if (sizeB > sizeA) status = "bigger";
                else if (sizeB < sizeA) status = "smaller";
                else status = "same";

                diffItems.Add((key, sizeA, sizeB, delta, status));
            }

            diffItems = diffItems
                .OrderBy(d => d.status == "new" ? 0 : d.status == "bigger" ? 1 : d.status == "same" ? 2 : d.status == "smaller" ? 3 : 4)
                .ThenByDescending(d => Math.Abs(d.delta))
                .ToList();

            using (var writer = new StreamWriter(path, false, Encoding.UTF8))
            {
                writer.WriteLine($"Name,{labelA}(bytes),{labelB}(bytes),Delta(bytes),Delta(KB),Status");
                foreach (var item in diffItems)
                {
                    writer.WriteLine($"{Escape(item.name)},{item.sizeA},{item.sizeB},{item.delta},{item.delta / 1024.0:F2},{item.status}");
                }
            }
        }

        #endregion

        #region Helpers

        static Dictionary<string, (ulong totalSize, int count)> GetNativeTypeStats(CachedSnapshot snapshot)
        {
            var stats = new Dictionary<string, (ulong totalSize, int count)>();
            var nativeObjects = snapshot.NativeObjects;
            var nativeTypes = snapshot.NativeTypes;

            for (int i = 0; i < nativeObjects.Count; i++)
            {
                var typeIndex = nativeObjects.NativeTypeArrayIndex[i];
                if (typeIndex < 0 || typeIndex >= nativeTypes.Count) continue;

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

        static Dictionary<string, (ulong totalSize, int count)> GetManagedTypeStats(CachedSnapshot snapshot)
        {
            var stats = new Dictionary<string, (ulong totalSize, int count)>();
            var managedObjects = snapshot.CrawledData.ManagedObjects;
            var typeDescriptions = snapshot.TypeDescriptions;

            for (int i = 0; i < managedObjects.Count; i++)
            {
                var obj = managedObjects[i];
                var typeIndex = obj.ITypeDescription;
                if (typeIndex < 0 || typeIndex >= typeDescriptions.Count) continue;

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

        static string GetArg(string[] args, string key)
        {
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == key)
                    return args[i + 1];
            }
            return null;
        }

        static string Escape(string field)
        {
            if (string.IsNullOrEmpty(field)) return field;
            if (field.Contains(",") || field.Contains("\"") || field.Contains("\n") || field.Contains("\r"))
                return $"\"{field.Replace("\"", "\"\"")}\"";
            return field;
        }

        #endregion
    }
}
