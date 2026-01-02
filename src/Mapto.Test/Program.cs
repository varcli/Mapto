using Mapto.Test;

public class Program
{
    static void Main(string[] args)
    {
        // 1. 运行功能验证
        FullCoverageTest.Run();

        // 2. 运行性能测试
        BenchmarkTest.Run();

        Console.ReadLine();
    }
}