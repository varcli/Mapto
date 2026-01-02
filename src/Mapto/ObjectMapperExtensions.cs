using System;
using System.Collections.Concurrent;
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
    /// <para>示例: var dto = entity.To&lt;UserDto&gt;();</para>
    /// </summary>
    public static TDestination Map<TDestination>(this object source) where TDestination : new()
    {
        if (source == null) return default(TDestination);
        return ExtensionCache<TDestination>.GetMapper(source.GetType())(source);
    }

    /// <summary>
    /// [推荐] 显式源类型转换
    /// <para>示例: var dto = entity.To&lt;UserEntity, UserDto&gt;();</para>
    /// </summary>
    public static TDestination Map<TSource, TDestination>(this TSource source) where TDestination : new()
    {
        return ObjectMapper.Map<TSource, TDestination>(source);
    }

    /// <summary>
    /// 映射到已有对象
    /// <para>示例: dto.MapTo(entity);</para>
    /// </summary>
    public static TDestination MapTo<TSource, TDestination>(this TSource source, TDestination existing)
    {
        return ObjectMapper.Map(source, existing);
    }

    private static class ExtensionCache<TDest>
    {
        private static readonly ConcurrentDictionary<Type, Func<object, TDest>> _cache = new ConcurrentDictionary<Type, Func<object, TDest>>();
        public static Func<object, TDest> GetMapper(Type srcType)
        {
            if (_cache.TryGetValue(srcType, out var func)) return func;
            var p = Expression.Parameter(typeof(object), "obj");
            var m = typeof(ObjectMapper).GetMethods(BindingFlags.Public | BindingFlags.Static).First(x => x.Name == "Map" && x.IsGenericMethodDefinition && x.GetGenericArguments().Length == 2 && x.GetParameters().Length == 2).MakeGenericMethod(srcType, typeof(TDest));
            var call = Expression.Call(m, Expression.Convert(p, srcType), Expression.Constant(null, typeof(Action<>).MakeGenericType(typeof(ObjectMapper.IMapConfiguration<,>).MakeGenericType(srcType, typeof(TDest)))));
            func = Expression.Lambda<Func<object, TDest>>(call, p).Compile();
            _cache.TryAdd(srcType, func);
            return func;
        }
    }
}
