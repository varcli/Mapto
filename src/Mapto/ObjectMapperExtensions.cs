using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;

namespace Mapto;

/// <summary>
/// 扩展方法支持
/// </summary>
public static class ObjectMapperExtensions
{
    /// <summary>
    /// [推荐] 转换为新对象
    /// </summary>
    public static TDestination Map<TDestination>(this object source) where TDestination : new()
    {
        if (source == null) return default(TDestination);
        // 使用缓存的委托处理 object -> TDest
        return ExtensionCache<TDestination>.GetMapper(source.GetType())(source);
    }

    /// <summary>
    /// [推荐] 转换为新对象 (显式源类型)
    /// </summary>
    public static TDestination Map<TSource, TDestination>(this TSource source, Action<ObjectMapper.IMapConfiguration<TSource, TDestination>> config = null) where TDestination : new()
    {
        return ObjectMapper.Map(source, config);
    }

    /// <summary>
    /// 映射到已有对象
    /// </summary>
    public static TDestination MapTo<TSource, TDestination>(this TSource source, TDestination existing, Action<ObjectMapper.IMapConfiguration<TSource, TDestination>> config = null)
    {
        return ObjectMapper.Map(source, existing, config);
    }

    /// <summary>
    /// 集合映射
    /// </summary>
    public static List<TDest> MapEach<TSrc, TDest>(this IEnumerable<TSrc> source, Action<ObjectMapper.IMapConfiguration<TSrc, TDest>> elementConfig = null) where TDest : new()
    {
        if (source == null) return new List<TDest>();

        // 简单场景走批量优化通道
        if (elementConfig == null)
        {
            // 注意：这里调用 ObjectMapper.Map 会进入 MapEnumerable 逻辑，比循环 Map 单个对象更快
            return ObjectMapper.Map<IEnumerable<TSrc>, List<TDest>>(source);
        }

        // 有自定义配置时，必须循环处理
        var list = new List<TDest>(source is ICollection<TSrc> c ? c.Count : 10);
        foreach (var item in source)
        {
            list.Add(ObjectMapper.Map<TSrc, TDest>(item, elementConfig));
        }
        return list;
    }

    private static class ExtensionCache<TDest>
    {
        private static readonly ConcurrentDictionary<Type, Func<object, TDest>> _cache = new ConcurrentDictionary<Type, Func<object, TDest>>();

        public static Func<object, TDest> GetMapper(Type srcType)
        {
            if (_cache.TryGetValue(srcType, out var func)) return func;

            var p = Expression.Parameter(typeof(object), "obj");

            var mapMethod = typeof(ObjectMapper).GetMethods(BindingFlags.Public | BindingFlags.Static)
                .First(m => m.Name == "Map"
                            && m.IsGenericMethodDefinition
                            && m.GetGenericArguments().Length == 2
                            && m.GetParameters().Length == 2);

            // 构造具体泛型方法: Map<srcType, TDest>
            var genericMethod = mapMethod.MakeGenericMethod(srcType, typeof(TDest));

            // 构造调用: ObjectMapper.Map<Src, Dest>((Src)obj, null)
            var call = Expression.Call(genericMethod,
                Expression.Convert(p, srcType),
                Expression.Constant(null, typeof(Action<>).MakeGenericType(typeof(ObjectMapper.IMapConfiguration<,>).MakeGenericType(srcType, typeof(TDest)))));

            func = Expression.Lambda<Func<object, TDest>>(call, p).Compile();
            _cache.TryAdd(srcType, func);
            return func;
        }
    }
}
