using System.Diagnostics;

namespace Mapto.Test
{
    public class BenchmarkTest
    {
        // 循环次数：100万次
        // 如果你的机器非常快，可以增加到 10,000,000
        private const int ITERATIONS = 1_000_000;

        public static void Run()
        {
            Console.WriteLine("==========================================================");
            Console.WriteLine($" 🚀 性能基准测试  | 循环次数: {ITERATIONS:N0}");
            Console.WriteLine("==========================================================");
            Console.WriteLine($"环境: .NET {Environment.Version} | 64Bit: {Environment.Is64BitProcess}");
            Console.WriteLine();

            var src = new BenchSrc
            {
                Id = 1001,
                Name = "Benchmark Data",
                Price = 199.99m,
                Date = DateTime.Now,
                Tags = new List<string> { "High", "Performance", "Code" },
                Status = BenchStatus.Active
            };

            // 1. [关键] 预热 (Warmup)
            // 让 JIT 编译代码，并触发 ObjectMapper 的静态构造函数和缓存构建
            Console.Write("正在预热 JIT & 缓存... ");
            ManualMap(src);
            ObjectMapper.Map<BenchSrc, BenchDest>(src);
            src.Map<BenchDest>();
            var reuseDest = new BenchDest();
            src.MapTo(reuseDest);
            Console.WriteLine("完成.\n");

            // =================================================
            // 测试 1: 创建新对象 (Create New)
            // =================================================
            Console.WriteLine("--- [场景 A: 创建新对象 (New Object)] ---");

            // Baseline: Native
            GC.Collect();
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < ITERATIONS; i++)
            {
                var d = ManualMap(src);
            }
            sw.Stop();
            var tNative = sw.ElapsedMilliseconds;
            PrintResult("1. Native Code (手写)", tNative, tNative);

            // Target: ObjectMapper.Map
            GC.Collect();
            sw.Restart();
            for (int i = 0; i < ITERATIONS; i++)
            {
                var d = ObjectMapper.Map<BenchSrc, BenchDest>(src);
            }
            sw.Stop();
            var tMap = sw.ElapsedMilliseconds;
            PrintResult("2. ObjectMapper.Map ", tMap, tNative);

            // Target: Extension .Map<T>
            GC.Collect();
            sw.Restart();
            for (int i = 0; i < ITERATIONS; i++)
            {
                var d = src.Map<BenchDest>();
            }
            sw.Stop();
            var tExt = sw.ElapsedMilliseconds;
            PrintResult("3. Extension .Map<T> ", tExt, tNative); // V14优化重点：应与 Map 持平

            // =================================================
            // 测试 2: 更新已有对象 (Update Existing / Zero Alloc)
            // =================================================
            Console.WriteLine("\n--- [场景 B: 更新已有对象 (0 GC)] ---");

            // Baseline: Native Update
            var target = new BenchDest();
            GC.Collect();
            sw.Restart();
            for (int i = 0; i < ITERATIONS; i++)
            {
                ManualUpdate(src, target);
            }
            sw.Stop();
            var tNativeUpd = sw.ElapsedMilliseconds;
            PrintResult("1. Native Update    ", tNativeUpd, tNativeUpd);

            // Target: MapTo
            GC.Collect();
            sw.Restart();
            for (int i = 0; i < ITERATIONS; i++)
            {
                src.MapTo(target);
            }
            sw.Stop();
            var tMapTo = sw.ElapsedMilliseconds;
            PrintResult("2. .MapTo(existing) ", tMapTo, tNativeUpd);

            Console.WriteLine("\n==========================================================");
        }

        static void PrintResult(string name, long time, long baseline)
        {
            double ratio = (double)time / baseline;
            ConsoleColor color = ConsoleColor.White;

            if (name.Contains("Native")) color = ConsoleColor.Cyan;
            else if (ratio <= 1.2) color = ConsoleColor.Green; // 优秀
            else if (ratio <= 2.0) color = ConsoleColor.Yellow; // 良好
            else color = ConsoleColor.Red; // 较慢

            var oldColor = Console.ForegroundColor;
            Console.ForegroundColor = color;
            Console.WriteLine($"{name.PadRight(25)} : {time,5} ms | Ratio: {ratio:F2}x");
            Console.ForegroundColor = oldColor;
        }

        // --- 手写对照组 ---
        static BenchDest ManualMap(BenchSrc s)
        {
            if (s == null) return null;
            return new BenchDest
            {
                Id = s.Id,
                Name = s.Name,
                Price = (double)s.Price, // decimal -> double
                Date = s.Date,
                Tags = s.Tags?.ToArray(), // List -> Array
                Status = (int)s.Status    // Enum -> Int
            };
        }

        static void ManualUpdate(BenchSrc s, BenchDest d)
        {
            if (s == null || d == null) return;
            d.Id = s.Id;
            d.Name = s.Name;
            d.Price = (double)s.Price;
            d.Date = s.Date;
            d.Tags = s.Tags?.ToArray(); // 注意：这里还是会产生数组分配，除非深拷贝逻辑更复杂
            d.Status = (int)s.Status;
        }
    }

    // --- 测试模型 ---
    public enum BenchStatus { Inactive = 0, Active = 1 }

    public class BenchSrc
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public decimal Price { get; set; }
        public DateTime Date { get; set; }
        public List<string> Tags { get; set; }
        public BenchStatus Status { get; set; }
    }

    public class BenchDest
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public double Price { get; set; } // 类型不同，需转换
        public DateTime Date { get; set; }
        public string[] Tags { get; set; } // 集合类型不同
        public int Status { get; set; }    // 枚举转整型
    }
}