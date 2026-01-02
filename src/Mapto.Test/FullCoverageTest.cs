namespace Mapto.Test;

public class FullCoverageTest
{
    public static void Run()
    {
        Console.WriteLine("==================================================");
        Console.WriteLine("      ObjectMapper 全类型深度覆盖测试");
        Console.WriteLine("==================================================");

        try
        {
            var src = CreateMegaSource();

            // 使用扩展方法 .To<T>() 进行测试
            var dest = ObjectMapper.Map<MegaSource, MegaDest>(src);

            // --- 1. 基础数值与可空类型 ---
            Console.WriteLine("\n[1. Primitives & Nullables]");
            Assert("Byte", (byte)255, dest.PropByte);
            Assert("Long", 9999999999L, dest.PropLong);
            Assert("Decimal", 123.456m, dest.PropDecimal);
            Assert("Int? -> Int (Val)", 100, dest.NullableInt_To_Int);
            Assert("Int? -> Int (Null)", 0, dest.NullInt_To_Int); // null 转默认值 0
            Assert("Int -> Int? (Val)", 999, dest.Int_To_NullableInt);

            // --- 2. 数值强转 (Casting) ---
            Console.WriteLine("\n[2. Numeric Casting]");
            Assert("Double -> Int (Truncate)", 99, dest.Double_To_Int); // 99.9 -> 99
            Assert("Float -> Decimal", 3.14m, Math.Round(dest.Float_To_Decimal, 2));

            // --- 3. 字符串宽容解析 (Magic Parsing) ---
            Console.WriteLine("\n[3. String Magic Parsing]");
            Assert("String -> Int", 1024, dest.Str_To_Int);
            Assert("String -> DateTime", new DateTime(2025, 1, 1), dest.Str_To_Date);
            Assert("String -> Guid", Guid.Parse("d0107c64-0740-4da6-a6e6-9917326b5c8c"), dest.Str_To_Guid);
            Assert("Empty String -> Int", 0, dest.EmptyStr_To_Int); // 空串转 0
            Assert("Empty String -> Date", DateTime.MinValue, dest.EmptyStr_To_Date); // 空串转 MinValue

            // --- 4. 布尔值宽容模式 ---
            Console.WriteLine("\n[4. Fuzzy Boolean]");
            Assert("String '1' -> True", true, dest.Bool_From_1);
            Assert("String 'yes' -> True", true, dest.Bool_From_Yes);
            Assert("String 'on' -> True", true, dest.Bool_From_On);
            Assert("String 'False' -> False", false, dest.Bool_From_False);

            // --- 5. 枚举测试 ---
            Console.WriteLine("\n[5. Enums]");
            Assert("Int -> Enum", MyStatus.Active, dest.Int_To_Enum);
            Assert("String -> Enum", MyStatus.Deleted, dest.Str_To_Enum);
            Assert("String -> Flags", MyFlags.Red | MyFlags.Blue, dest.Str_To_Flags); // "Red, Blue" -> 3

            // --- 6. 集合与数组 ---
            Console.WriteLine("\n[6. Collections]");
            Assert("List -> Array", true, dest.List_To_Array is int[]);
            Assert("Array Count", 3, dest.List_To_Array.Length);
            Assert("Array -> List", true, dest.Array_To_List is List<string>);
            Assert("String[] -> HashSet", true, dest.StrArray_To_HashSet is HashSet<string>);
            Assert("HashSet Count (Dedup)", 2, dest.StrArray_To_HashSet.Count); // "A", "B", "A" -> 2 items
            Assert("Null List -> Empty", 0, dest.NullList_To_Array?.Length); // 防御：null转空数组

            // --- 7. 字典转换 ---
            Console.WriteLine("\n[7. Dictionaries]");
            // Key: "10" -> 10, Value: "99.9" -> 99.9m
            Assert("Dict Key (String->Int)", true, dest.DictConvert.ContainsKey(10));
            Assert("Dict Val (String->Dec)", 99.9m, dest.DictConvert[10]);

            // --- 8. 扁平化 (Flattening) ---
            Console.WriteLine("\n[8. Flattening & Deep Null Safety]");
            // Source.Inner.City -> Dest.InnerCity
            Assert("Flattening (L1)", "New York", dest.InnerCity);
            // Source.Deep.A.B -> Dest.DeepAB (Deep is null in source)
            Assert("Flattening Null Safety", null, dest.DeepAB); // 不应该崩，应为null

            Console.WriteLine("\n--------------------------------------------------");
            Console.WriteLine("✅  全类型测试完美通过 (All Types Passed)");
            Console.WriteLine("--------------------------------------------------");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\n❌  测试失败: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
        }
    }

    // --- 断言辅助 ---
    static void Assert(string name, object expected, object actual)
    {
        bool pass = false;
        if (expected == null && actual == null) pass = true;
        else if (expected != null) pass = expected.Equals(actual);

        var color = Console.ForegroundColor;
        Console.ForegroundColor = pass ? ConsoleColor.Green : ConsoleColor.Red;
        Console.WriteLine($"[{(pass ? "PASS" : "FAIL")}] {name,-25} | Exp: {expected ?? "null"} | Act: {actual ?? "null"}");
        Console.ForegroundColor = color;
        if (!pass) throw new Exception($"Assertion failed: {name}");
    }

    // --- 数据工厂 ---
    static MegaSource CreateMegaSource()
    {
        return new MegaSource
        {
            // Primitives
            PropByte = 255,
            PropLong = 9999999999,
            PropDecimal = 123.456m,
            NullableInt_To_Int = 100,
            NullInt_To_Int = null,
            Int_To_NullableInt = 999,
            Double_To_Int = 99.9d,
            Float_To_Decimal = 3.14f,

            // Strings
            Str_To_Int = "1024",
            Str_To_Date = "2025-01-01",
            Str_To_Guid = "d0107c64-0740-4da6-a6e6-9917326b5c8c",
            EmptyStr_To_Int = "",
            EmptyStr_To_Date = "   ",

            // Bools
            Bool_From_1 = "1",
            Bool_From_Yes = "yes",
            Bool_From_On = "ON",
            Bool_From_False = "False",

            // Enums
            Int_To_Enum = 1,
            Str_To_Enum = "Deleted",
            Str_To_Flags = "Red, Blue",

            // Collections
            List_To_Array = new List<int> { 1, 2, 3 },
            Array_To_List = new[] { "A", "B", "C" },
            StrArray_To_HashSet = new[] { "A", "B", "A" }, // Duplicate 'A'
            NullList_To_Array = null,

            // Dictionary
            DictConvert = new Dictionary<string, string> { { "10", "99.9" }, { "20", "88.8" } },

            // Flattening
            Inner = new NestedSrc { City = "New York" },
            Deep = null // Test null safety
        };
    }
}

// ================== 复杂模型定义 ==================

public enum MyStatus { Inactive = 0, Active = 1, Deleted = 2 }
[Flags] public enum MyFlags { None = 0, Red = 1, Blue = 2, Green = 4 }

public class MegaSource
{
    // 1. Primitives
    public byte PropByte { get; set; }
    public long PropLong { get; set; }
    public decimal PropDecimal { get; set; }
    public int? NullableInt_To_Int { get; set; }
    public int? NullInt_To_Int { get; set; }
    public int Int_To_NullableInt { get; set; }
    public double Double_To_Int { get; set; }
    public float Float_To_Decimal { get; set; }

    // 2. String Parsing
    public string Str_To_Int { get; set; }
    public string Str_To_Date { get; set; }
    public string Str_To_Guid { get; set; }
    public string EmptyStr_To_Int { get; set; }
    public string EmptyStr_To_Date { get; set; }

    // 3. Bools
    public string Bool_From_1 { get; set; }
    public string Bool_From_Yes { get; set; }
    public string Bool_From_On { get; set; }
    public string Bool_From_False { get; set; }

    // 4. Enums
    public int Int_To_Enum { get; set; }
    public string Str_To_Enum { get; set; }
    public string Str_To_Flags { get; set; }

    // 5. Collections
    public List<int> List_To_Array { get; set; }
    public string[] Array_To_List { get; set; }
    public string[] StrArray_To_HashSet { get; set; }
    public List<string> NullList_To_Array { get; set; }

    // 6. Dictionary
    public Dictionary<string, string> DictConvert { get; set; }

    // 7. Nested
    public NestedSrc Inner { get; set; }
    public NestedSrc Deep { get; set; } // Will be null
}

public class MegaDest
{
    public byte PropByte { get; set; }
    public long PropLong { get; set; }
    public decimal PropDecimal { get; set; }
    public int NullableInt_To_Int { get; set; }
    public int NullInt_To_Int { get; set; }
    public int? Int_To_NullableInt { get; set; }
    public int Double_To_Int { get; set; }
    public decimal Float_To_Decimal { get; set; }

    public int Str_To_Int { get; set; }
    public DateTime Str_To_Date { get; set; }
    public Guid Str_To_Guid { get; set; }
    public int EmptyStr_To_Int { get; set; }
    public DateTime EmptyStr_To_Date { get; set; }

    public bool Bool_From_1 { get; set; }
    public bool Bool_From_Yes { get; set; }
    public bool Bool_From_On { get; set; }
    public bool Bool_From_False { get; set; }

    public MyStatus Int_To_Enum { get; set; }
    public MyStatus Str_To_Enum { get; set; }
    public MyFlags Str_To_Flags { get; set; }

    public int[] List_To_Array { get; set; }
    public List<string> Array_To_List { get; set; }
    public HashSet<string> StrArray_To_HashSet { get; set; }
    public int[] NullList_To_Array { get; set; }

    public Dictionary<int, decimal> DictConvert { get; set; }

    // Flattening Mappings
    public string InnerCity { get; set; } // Maps to Inner.City
    public string DeepAB { get; set; }    // Maps to Deep.A.B (Safe Null)
}

public class NestedSrc
{
    public string City { get; set; }
    public NestedInner A { get; set; }
}
public class NestedInner
{
    public string B { get; set; }
}