using System;

namespace Mapto;

/// <summary>
/// 标记属性跳过映射
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
public class NoMapAttribute : Attribute
{
}

/// <summary>
/// 指定映射的源属性名
/// <para>示例: [MapAs("ExtraProperties")] public string Meta { get; set; }</para>
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
public class MapAsAttribute : Attribute
{
    public string SourcePropertyName { get; }

    public MapAsAttribute(string sourcePropertyName)
    {
        SourcePropertyName = sourcePropertyName;
    }
}
