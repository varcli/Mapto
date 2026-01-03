using System;
using System.Linq;

namespace Mapto;

/// <summary>
/// EF Core 投影扩展 (IQueryable -> SQL)
/// </summary>
public static class QueryableExtensions
{
    /// <summary>
    /// 将 IQueryable 投影为目标类型 (生成纯净的 Select SQL)
    /// <para>示例: db.Users.ProjectTo&lt;UserDto&gt;().ToList();</para>
    /// </summary>
    public static IQueryable<TDestination> ProjectTo<TDestination>(this IQueryable source)
    {
        if (source == null) throw new ArgumentNullException(nameof(source));

        var sourceType = source.ElementType;
        var destType = typeof(TDestination);

        // 1. 获取或构建投影表达式 x => new Dto { ... }
        var projection = QueryableBuilder.GetProjection(sourceType, destType);

        // 2. 调用 source.Select(projection)
        // 因为 source 是非泛型 IQueryable，需通过反射调用泛型 Select
        var selectMethod = QueryableBuilder.QueryableSelectMethod.MakeGenericMethod(sourceType, destType);
        return (IQueryable<TDestination>)selectMethod.Invoke(null, new object[] { source, projection });
    }

    /// <summary>
    /// ProjectTo 的泛型版本
    /// </summary>
    public static IQueryable<TDestination> ProjectTo<TSource, TDestination>(this IQueryable<TSource> source)
    {
        var expression = QueryableBuilder<TSource, TDestination>.Expression;
        return source.Select(expression);
    }
}