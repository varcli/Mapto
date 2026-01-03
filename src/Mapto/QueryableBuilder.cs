using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;

namespace Mapto;

/// <summary>
/// 内部构建器引擎
/// </summary>
internal static class QueryableBuilder
{
    // 缓存 Key: (SourceType, DestType) -> Expression
    private static readonly ConcurrentDictionary<(Type, Type), Expression> _cache = new ConcurrentDictionary<(Type, Type), Expression>();

    // 反射元数据缓存
    internal static readonly MethodInfo QueryableSelectMethod = typeof(Queryable).GetMethods().First(m => m.Name == "Select" && m.GetParameters().Length == 2 && m.GetParameters()[1].ParameterType.GetGenericArguments()[0].GetGenericTypeDefinition() == typeof(Expression<>));
    internal static readonly MethodInfo EnumerableSelectMethod = typeof(Enumerable).GetMethods().First(m => m.Name == "Select" && m.GetParameters().Length == 2 && m.GetParameters()[1].ParameterType.GetGenericArguments().Length == 2);
    internal static readonly MethodInfo EnumerableToListMethod = typeof(Enumerable).GetMethod("ToList");

    // 获取投影表达式入口
    public static Expression GetProjection(Type srcType, Type destType)
    {
        return _cache.GetOrAdd((srcType, destType), key =>
        {
            // 反射调用泛型构建器，确保类型安全
            var builderType = typeof(QueryableBuilder<,>).MakeGenericType(key.Item1, key.Item2);
            return (Expression)builderType.GetField("Expression").GetValue(null);
        });
    }
}

/// <summary>
/// 泛型构建器 (针对特定类型对)
/// </summary>
internal static class QueryableBuilder<TSource, TDestination>
{
    public static readonly Expression<Func<TSource, TDestination>> Expression;

    static QueryableBuilder()
    {
        Expression = BuildExpression(0);
    }

    // 递归构建表达式树
    // depth: 当前递归深度，防止循环引用死锁
    internal static Expression<Func<TSource, TDestination>> BuildExpression(int depth)
    {
        // 熔断机制：超过 10 层嵌套停止映射，防止 StackOverflow
        if (depth > 10) return x => default;

        var sourceType = typeof(TSource);
        var destType = typeof(TDestination);
        var parameter = System.Linq.Expressions.Expression.Parameter(sourceType, "x");

        var bindings = new List<MemberBinding>();
        var destProps = destType.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanWrite);

        foreach (var destProp in destProps)
        {
            // 1. 查找源属性 (支持自动扁平化: User.Name -> UserName)
            System.Linq.Expressions.Expression sourceExpression = FindSourceExpression(parameter, sourceType, destProp.Name);

            if (sourceExpression != null)
            {
                // 2. 类型修正 (处理 int?->int, List->List 等)
                var targetExpression = FixType(sourceExpression, destProp.PropertyType, depth);

                if (targetExpression != null)
                {
                    bindings.Add(System.Linq.Expressions.Expression.Bind(destProp, targetExpression));
                }
            }
        }

        var body = System.Linq.Expressions.Expression.MemberInit(System.Linq.Expressions.Expression.New(destType), bindings);
        return System.Linq.Expressions.Expression.Lambda<Func<TSource, TDestination>>(body, parameter);
    }

    // 查找源属性表达式 (递归处理 Flattening)
    private static System.Linq.Expressions.Expression FindSourceExpression(System.Linq.Expressions.Expression currentExp, Type currentType, string remainingName)
    {
        // A. 直接匹配 (IgnoreCase)
        var directProp = currentType.GetProperty(remainingName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
        if (directProp != null)
        {
            return System.Linq.Expressions.Expression.Property(currentExp, directProp);
        }

        // B. 扁平化匹配 (Flattening)
        foreach (var prop in currentType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (remainingName.StartsWith(prop.Name, StringComparison.OrdinalIgnoreCase))
            {
                var subName = remainingName.Substring(prop.Name.Length);
                if (string.IsNullOrEmpty(subName)) continue;

                // 递归深入下一层
                var subExp = System.Linq.Expressions.Expression.Property(currentExp, prop);
                var found = FindSourceExpression(subExp, prop.PropertyType, subName);
                if (found != null) return found;
            }
        }
        return null;
    }

    // 类型适配与修正
    private static System.Linq.Expressions.Expression FixType(System.Linq.Expressions.Expression srcExp, Type destType, int depth)
    {
        var srcType = srcExp.Type;
        if (srcType == destType) return srcExp;

        var uSrc = Nullable.GetUnderlyingType(srcType);
        var uDest = Nullable.GetUnderlyingType(destType);

        // 1. 集合投影 (List<Entity> -> List<Dto>)
        // 生成逻辑: src.Orders.Select(o => new OrderDto{...}).ToList()
        if (IsGenericEnumerable(srcType) && IsGenericEnumerable(destType))
        {
            var srcItemType = GetGenericArgument(srcType);
            var destItemType = GetGenericArgument(destType);

            if (srcItemType != null && destItemType != null)
            {
                // 递归获取子项的投影表达式
                // 此时必须使用反射调用泛型 BuildExpression，因为类型是在运行时确定的
                var subBuilderType = typeof(QueryableBuilder<,>).MakeGenericType(srcItemType, destItemType);
                var subExpression = (System.Linq.Expressions.Expression)subBuilderType.GetMethod("BuildExpression", BindingFlags.NonPublic | BindingFlags.Static)
                    .Invoke(null, new object[] { depth + 1 });

                if (subExpression == null) return null;

                // 构建 .Select()
                var select = QueryableBuilder.EnumerableSelectMethod.MakeGenericMethod(srcItemType, destItemType);
                var selectCall = System.Linq.Expressions.Expression.Call(select, srcExp, subExpression);

                // 关键修正: 如果目标是 List/ICollection，必须调用 .ToList()，否则 Expression Bind 会失败
                if (typeof(List<>).MakeGenericType(destItemType).IsAssignableFrom(destType) ||
                    typeof(ICollection<>).MakeGenericType(destItemType).IsAssignableFrom(destType) ||
                    typeof(IList<>).MakeGenericType(destItemType).IsAssignableFrom(destType))
                {
                    var toList = QueryableBuilder.EnumerableToListMethod.MakeGenericMethod(destItemType);
                    return System.Linq.Expressions.Expression.Call(toList, selectCall);
                }

                // 如果目标只是 IEnumerable，直接返回 Select 结果
                return selectCall;
            }
        }

        // 2. 可空类型处理 (int? -> int)
        // 生成逻辑: src ?? default(int)
        if (uSrc != null && uDest == null && uSrc == destType)
        {
            return System.Linq.Expressions.Expression.Coalesce(srcExp, System.Linq.Expressions.Expression.Default(destType));
        }

        // 3. 基础类型转换 (int -> double)
        // 生成逻辑: (double)src
        if (IsBasicType(srcType) && IsBasicType(destType))
        {
            return System.Linq.Expressions.Expression.Convert(srcExp, destType);
        }

        return null; // 无法转换则忽略
    }

    private static bool IsGenericEnumerable(Type t) => t.IsGenericType && typeof(System.Collections.IEnumerable).IsAssignableFrom(t);
    private static Type GetGenericArgument(Type t) => t.GetGenericArguments().FirstOrDefault();
    private static bool IsBasicType(Type t) => t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(decimal) || t == typeof(DateTime) || t == typeof(Guid) || Nullable.GetUnderlyingType(t) != null;
}
