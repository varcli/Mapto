using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;

namespace Mapto;

/// <summary>
/// 高性能、零配置、轻量级对象映射器
/// </summary>
public static class ObjectMapper
{
    #region 全局配置
    public static class GlobalConfig
    {
        /// <summary>
        /// 递归最大深度，防止循环引用导致栈溢出
        /// </summary>
        public static int MaxDepth = 30;

        /// <summary>
        /// 是否将空字符串("")映射为目标类型的默认值(如 int 的 0)
        /// </summary>
        public static bool MapEmptyStringToDefault = true;
    }
    #endregion

    #region 内部状态与缓存
    [ThreadStatic]
    private static int _currentDepth;

    private static readonly MethodInfo _mapMethod = typeof(ObjectMapper).GetMethods(BindingFlags.Public | BindingFlags.Static)
        .First(m => m.Name == nameof(Map) && m.IsGenericMethodDefinition && m.GetGenericArguments().Length == 2 && m.GetParameters().Length == 2);

    private static readonly MethodInfo _mapEnum = typeof(ObjectMapper).GetMethod(nameof(MapEnumerable), BindingFlags.NonPublic | BindingFlags.Static);
    private static readonly MethodInfo _mapDict = typeof(ObjectMapper).GetMethod(nameof(MapDictionary), BindingFlags.NonPublic | BindingFlags.Static);

    // Setter 缓存，用于 MapConfiguration
    private static readonly ConcurrentDictionary<PropertyInfo, Delegate> _setterCache = new ConcurrentDictionary<PropertyInfo, Delegate>();
    #endregion

    #region 公共 API

    /// <summary>
    /// 将源对象映射为目标类型的新对象
    /// </summary>
    public static TDestination Map<TSource, TDestination>(TSource source, Action<IMapConfiguration<TSource, TDestination>> config = null)
    {
        if (source == null) return default(TDestination);
        try
        {
            _currentDepth++;
            if (_currentDepth > GlobalConfig.MaxDepth) return default(TDestination);

            // 如果没有配置，直接使用缓存的快速委托
            if (config == null) return MapperCache<TSource, TDestination>.MapperFunc(source);

            // 如果有配置，先生成默认映射，再应用配置
            var destination = MapperCache<TSource, TDestination>.MapperFunc(source);
            if (!MapperCache<TSource, TDestination>.IsBasicType)
            {
                config(new MapConfiguration<TSource, TDestination>(source, destination));
            }
            return destination;
        }
        finally { _currentDepth--; }
    }

    /// <summary>
    /// 将源对象合并到现有的目标对象中
    /// </summary>
    public static TDestination Map<TSource, TDestination>(TSource source, TDestination existing, Action<IMapConfiguration<TSource, TDestination>> config = null)
    {
        if (source == null) return existing;
        if (existing == null) return Map(source, config);
        try
        {
            _currentDepth++;
            if (_currentDepth > GlobalConfig.MaxDepth) return existing;

            MapperCache<TSource, TDestination>.MergeAction(source, existing);

            if (config != null && !MapperCache<TSource, TDestination>.IsBasicType)
            {
                config(new MapConfiguration<TSource, TDestination>(source, existing));
            }
            return existing;
        }
        finally { _currentDepth--; }
    }

    #endregion

    #region 核心引擎 (MapperCache)
    private static class MapperCache<TSource, TDestination>
    {
        private static Func<TSource, TDestination> _mapperFunc;
        private static Action<TSource, TDestination> _mergeAction;
        public static readonly bool IsBasicType;

        static MapperCache()
        {
            // 判断目标是否为基础类型（无需属性映射）
            IsBasicType = ObjectMapper.IsBasicType(typeof(TDestination));
        }

        public static Func<TSource, TDestination> MapperFunc
        {
            get
            {
                if (_mapperFunc == null)
                {
                    var sType = typeof(TSource);
                    var dType = typeof(TDestination);

                    if (IsDictionary(sType) && IsDictionary(dType))
                        _mapperFunc = CompileDictionaryMapper();
                    else if (IsEnumerable(sType) && IsEnumerable(dType))
                        _mapperFunc = CompileEnumerableMapper();
                    else if (ObjectMapper.IsBasicType(sType) || ObjectMapper.IsBasicType(dType))
                        _mapperFunc = CompileBasicTypeMapper();
                    else
                        _mapperFunc = CompileObjectMapper();
                }
                return _mapperFunc;
            }
        }

        public static Action<TSource, TDestination> MergeAction
        {
            get
            {
                if (_mergeAction == null)
                {
                    var sType = typeof(TSource);
                    var dType = typeof(TDestination);

                    if (IsDictionary(sType) && IsDictionary(dType))
                        _mergeAction = (s, d) => { var n = MapperFunc(s); CopyDictionary(n, d as IDictionary); };
                    else if (IsEnumerable(sType) && IsEnumerable(dType) || ObjectMapper.IsBasicType(sType) || ObjectMapper.IsBasicType(dType))
                        _mergeAction = (s, d) => { /* 基础类型和集合无法"原地合并"内容，通常是替换引用，故忽略 */ };
                    else
                        _mergeAction = CompileMergeDelegate();
                }
                return _mergeAction;
            }
        }

        private static Func<TSource, TDestination> CompileBasicTypeMapper()
        {
            var p = Expression.Parameter(typeof(TSource), "source");
            var c = TryConvert(p, typeof(TSource), typeof(TDestination)) ?? Expression.Default(typeof(TDestination));
            return Expression.Lambda<Func<TSource, TDestination>>(c, p).Compile();
        }

        private static Func<TSource, TDestination> CompileEnumerableMapper()
        {
            var p = Expression.Parameter(typeof(TSource), "source");
            var itemSrc = GetElementType(typeof(TSource));
            var itemDest = GetElementType(typeof(TDestination));

            // 调用 MapEnumerable<TSrc, TDest>(src, destType)
            var method = _mapEnum.MakeGenericMethod(itemSrc, itemDest);
            var call = Expression.Call(method,
                Expression.Convert(p, typeof(IEnumerable<>).MakeGenericType(itemSrc)),
                Expression.Constant(typeof(TDestination)));

            var castedResult = Expression.Convert(call, typeof(TDestination));
            return Expression.Lambda<Func<TSource, TDestination>>(castedResult, p).Compile();
        }

        private static Func<TSource, TDestination> CompileDictionaryMapper()
        {
            var p = Expression.Parameter(typeof(TSource), "source");
            var argsS = typeof(TSource).GetGenericArguments();
            var argsD = typeof(TDestination).GetGenericArguments();

            // 调用 MapDictionary<SK, SV, DK, DV>(src)
            var method = _mapDict.MakeGenericMethod(argsS[0], argsS[1], argsD[0], argsD[1]);
            var call = Expression.Call(method, Expression.Convert(p, typeof(IDictionary<,>).MakeGenericType(argsS[0], argsS[1])));

            var castedResult = Expression.Convert(call, typeof(TDestination));
            return Expression.Lambda<Func<TSource, TDestination>>(castedResult, p).Compile();
        }

        private static Func<TSource, TDestination> CompileObjectMapper()
        {
            var p = Expression.Parameter(typeof(TSource), "source");

            // 检查目标类型构造函数
            var defaultCtor = typeof(TDestination).GetConstructor(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);
            if (defaultCtor == null)
            {
                // 可能是结构体或没有无参构造函数，尝试抛出更有意义的错误
                if (!typeof(TDestination).IsValueType)
                {
                    throw new InvalidOperationException($"Type '{typeof(TDestination).FullName}' needs a parameterless constructor for automatic mapping.");
                }
            }

            var newExp = Expression.New(typeof(TDestination));
            var bindings = new List<MemberBinding>();

            foreach (var dProp in typeof(TDestination).GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!dProp.CanWrite) continue;
                var val = BuildValueExpression(p, typeof(TSource), dProp);
                if (val != null) bindings.Add(Expression.Bind(dProp, val));
            }

            var init = Expression.MemberInit(newExp, bindings);

            // Null 检查
            Expression body = IsNullable(typeof(TSource))
                ? Expression.Condition(Expression.Equal(p, Expression.Constant(null, typeof(TSource))), Expression.Default(typeof(TDestination)), init)
                : (Expression)init;

            return Expression.Lambda<Func<TSource, TDestination>>(body, p).Compile();
        }

        private static Action<TSource, TDestination> CompileMergeDelegate()
        {
            var pSrc = Expression.Parameter(typeof(TSource), "source");
            var pDest = Expression.Parameter(typeof(TDestination), "dest");
            var list = new List<Expression>();

            foreach (var dProp in typeof(TDestination).GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!dProp.CanWrite) continue;
                var val = BuildValueExpression(pSrc, typeof(TSource), dProp);
                if (val != null) list.Add(Expression.Assign(Expression.Property(pDest, dProp), val));
            }

            if (list.Count == 0) return (s, d) => { };
            return Expression.Lambda<Action<TSource, TDestination>>(Expression.Block(list), pSrc, pDest).Compile();
        }

        private static void CopyDictionary(object src, IDictionary dest)
        {
            if (dest == null || src == null) return;
            dest.Clear();
            foreach (DictionaryEntry e in (src as IDictionary)) dest.Add(e.Key, e.Value);
        }
    }
    #endregion

    #region 表达式树构建 (Expression Building)
    private static Expression BuildValueExpression(Expression pSrc, Type sType, PropertyInfo dProp)
    {
        if (dProp.IsDefined(typeof(NoMapAttribute), false)) return null;

        Expression val = null;
        var mapAsAttr = dProp.GetCustomAttributes(typeof(MapAsAttribute), false).FirstOrDefault() as MapAsAttribute;

        if (mapAsAttr != null)
        {
            var sourceProp = sType.GetProperty(mapAsAttr.SourcePropertyName, BindingFlags.Public | BindingFlags.Instance);
            if (sourceProp != null && sourceProp.CanRead)
                val = Expression.Property(pSrc, sourceProp);
        }
        else
        {
            var direct = sType.GetProperty(dProp.Name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (direct != null && direct.CanRead) val = Expression.Property(pSrc, direct);
            else val = FindFlattenedProperty(pSrc, sType, dProp.Name);
        }

        if (val != null) return TryConvert(val, val.Type, dProp.PropertyType);
        return null;
    }

    private static Expression FindFlattenedProperty(Expression src, Type sType, string dName)
    {
        foreach (var prop in sType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (dName.StartsWith(prop.Name, StringComparison.OrdinalIgnoreCase))
            {
                var nextName = dName.Substring(prop.Name.Length);
                if (string.IsNullOrEmpty(nextName)) continue;

                var nextExp = Expression.Property(src, prop);
                var child = FindFlattenedProperty(nextExp, prop.PropertyType, nextName);

                // 尝试匹配属性名
                if (child == null)
                {
                    var match = prop.PropertyType.GetProperties().FirstOrDefault(p => p.Name.Equals(nextName, StringComparison.OrdinalIgnoreCase));
                    if (match != null) child = Expression.Property(nextExp, match);
                }

                if (child != null)
                {
                    // 路径中的空值检查
                    return IsNullable(prop.PropertyType)
                        ? Expression.Condition(Expression.Equal(nextExp, Expression.Constant(null, prop.PropertyType)), Expression.Default(child.Type), child)
                        : child;
                }
            }
        }
        return null;
    }

    private static Expression TryConvert(Expression src, Type sType, Type dType)
    {
        if (dType == sType && IsBasicType(sType)) return src;

        var uDest = Nullable.GetUnderlyingType(dType) ?? dType;
        var uSrc = Nullable.GetUnderlyingType(sType) ?? sType;

        // 1. 基础类型转换
        if (IsBasicType(uSrc) && IsBasicType(uDest))
            return BuildBasicTypeExpression(src, sType, dType, uSrc, uDest);

        // 2. 集合与字典转换
        if (IsDictionary(sType) && IsDictionary(dType))
        {
            var argsS = sType.GetGenericArguments();
            var argsD = dType.GetGenericArguments();
            var call = Expression.Call(
                _mapDict.MakeGenericMethod(argsS[0], argsS[1], argsD[0], argsD[1]),
                Expression.Convert(src, typeof(IDictionary<,>).MakeGenericType(argsS[0], argsS[1]))
            );
            return Expression.Convert(call, dType);
        }
        if (IsEnumerable(sType) && IsEnumerable(dType))
        {
            var iS = GetElementType(sType);
            var iD = GetElementType(dType);
            var call = Expression.Call(
                _mapEnum.MakeGenericMethod(iS, iD),
                Expression.Convert(src, typeof(IEnumerable<>).MakeGenericType(iS)),
                Expression.Constant(dType)
            );
            return Expression.Convert(call, dType);
        }

        // 3. 复杂对象递归转换
        if (!IsBasicType(sType) && !IsBasicType(dType))
        {
            // 生成 ObjectMapper.Map<S,D>(src, null)
            return Expression.Call(
                _mapMethod.MakeGenericMethod(sType, dType),
                src,
                Expression.Constant(null, typeof(Action<>).MakeGenericType(typeof(IMapConfiguration<,>).MakeGenericType(sType, dType)))
            );
        }
        return null;
    }

    private static Expression BuildBasicTypeExpression(Expression s, Type sType, Type dType, Type uSrc, Type uDest)
    {
        Expression val = sType != uSrc ? Expression.Property(s, "Value") : s;
        Expression res = null;

        // String -> Other
        if (sType == typeof(string))
        {
            if (IsNumeric(uDest) || uDest == typeof(DateTime) || uDest == typeof(Guid))
            {
                var m = uDest.GetMethod("Parse", new[] { typeof(string) });
                if (m != null) res = Expression.Call(m, s);
            }
            else if (uDest.IsEnum)
            {
                var m = typeof(Enum).GetMethod("Parse", new[] { typeof(Type), typeof(string), typeof(bool) });
                res = Expression.Convert(Expression.Call(m, Expression.Constant(uDest), s, Expression.Constant(true)), uDest);
            }
            else if (uDest == typeof(bool)) res = Expression.Call(typeof(ObjectMapper).GetMethod("ParseBoolRelaxed", BindingFlags.NonPublic | BindingFlags.Static), s);
            else if (uDest == typeof(char)) res = Expression.Call(typeof(ObjectMapper).GetMethod("ParseCharRelaxed", BindingFlags.NonPublic | BindingFlags.Static), s);

            // Handle string empty/null
            if (res != null && uDest.IsValueType && GlobalConfig.MapEmptyStringToDefault)
            {
                var check = typeof(string).GetMethod("IsNullOrWhiteSpace", new[] { typeof(string) });
                var def = Expression.Default(dType);
                res = Expression.Condition(Expression.Call(check, s), def, dType != uDest ? Expression.Convert(res, dType) : res);
                return res;
            }
        }

        if (res == null)
        {
            // Numeric/Enum/Primitive conversions
            if ((IsNumeric(uSrc) && IsNumeric(uDest)) || (uSrc.IsEnum || uDest.IsEnum))
                res = Expression.Convert(val, uDest);
            else if (dType == typeof(string))
            {
                var m = typeof(object).GetMethod("ToString");
                res = IsNullable(sType)
                    ? Expression.Condition(Expression.Equal(s, Expression.Constant(null, sType)), Expression.Constant(null, typeof(string)), Expression.Call(s, m))
                    : Expression.Call(s, m);
                return res;
            }
        }

        if (res == null) return null;
        if (dType != uDest && res.Type == uDest) res = Expression.Convert(res, dType);
        if (sType != uSrc) return Expression.Condition(Expression.Property(s, "HasValue"), res, Expression.Default(dType)); // Nullable -> Value
        return res;
    }
    #endregion

    #region 运行时辅助方法 (Runtime Helpers)

    private static object MapEnumerable<TSrc, TDest>(IEnumerable<TSrc> src, Type dCollType)
    {
        if (src == null) return dCollType.IsArray ? (object)new TDest[0] : new List<TDest>();

        // 尝试获取集合数量
        int count = (src as ICollection)?.Count ?? (src as ICollection<TSrc>)?.Count ?? 0;

        // --- 优化 1: 目标是数组的处理 ---
        if (dCollType.IsArray)
        {
            // [极速模式] 基础类型一致，直接内存块拷贝 (如 byte[] -> byte[], int[] -> int[])
            // 注意：仅限 Primitive 和 String，确保是深拷贝或不可变类型
            if (typeof(TSrc) == typeof(TDest) && (typeof(TSrc).IsPrimitive || typeof(TSrc) == typeof(string)))
            {
                if (src is TSrc[] srcArr)
                {
                    var destArr = new TDest[srcArr.Length];
                    Array.Copy(srcArr, destArr, srcArr.Length);
                    return destArr;
                }
            }

            // [零重新分配模式] 已知数量，直接创建数组填充，避免 List -> ToArray 的二次分配
            if (count > 0)
            {
                var destArr = new TDest[count];
                var func = MapperCache<TSrc, TDest>.MapperFunc;

                // 针对 IList 使用索引访问通常比 foreach 略快
                if (src is IList<TSrc> srcList)
                {
                    for (int i = 0; i < count; i++) destArr[i] = func(srcList[i]);
                }
                else
                {
                    int i = 0;
                    foreach (var item in src) destArr[i++] = func(item);
                }
                return destArr;
            }

            // 无法获取数量（如 yield return），只能退化到 List
            var fallbackList = new List<TDest>();
            var f = MapperCache<TSrc, TDest>.MapperFunc;
            foreach (var item in src) fallbackList.Add(f(item));
            return fallbackList.ToArray();
        }

        // --- 常规集合处理 (List, HashSet 等) ---

        // 预分配内存 (Capacity) 减少扩容开销
        var list = count > 0 ? new List<TDest>(count) : new List<TDest>();
        var mapper = MapperCache<TSrc, TDest>.MapperFunc;
        foreach (var i in src) list.Add(mapper(i));

        if (dCollType.IsGenericType)
        {
            var def = dCollType.GetGenericTypeDefinition();
            if (def == typeof(List<>) || def == typeof(IList<>) || def == typeof(IEnumerable<>)) return list;
            if (def == typeof(HashSet<>) || def == typeof(ISet<>)) return new HashSet<TDest>(list);
            if (def == typeof(LinkedList<>)) return new LinkedList<TDest>(list);
        }

        return list;
    }

    private static Dictionary<DK, DV> MapDictionary<SK, SV, DK, DV>(IDictionary<SK, SV> src)
    {
        if (src == null) return new Dictionary<DK, DV>();

        // --- 优化 2: 相同类型的快速克隆 ---
        // 如果 Key 和 Value 类型完全一致，且是基础类型（避免引用对象的浅拷贝问题），直接使用构造函数拷贝
        if (typeof(SK) == typeof(DK) && typeof(SV) == typeof(DV) &&
            ObjectMapper.IsBasicType(typeof(SK)) && ObjectMapper.IsBasicType(typeof(SV)))
        {
            // 这是一个 O(N) 操作，但比循环 Add 快，因为通过内部 bucket 批量处理
            return new Dictionary<DK, DV>((IDictionary<DK, DV>)src);
        }

        var fk = MapperCache<SK, DK>.MapperFunc;
        var fv = MapperCache<SV, DV>.MapperFunc;

        // 预分配 Capacity
        var res = new Dictionary<DK, DV>(src.Count);

        foreach (var kv in src)
        {
            var k = fk(kv.Key);
            // 只有 Key 不为 null 且不存在时才添加 (安全性检查)
            if (k != null && !res.ContainsKey(k))
                res.Add(k, fv(kv.Value));
        }
        return res;
    }

    private static bool IsNumeric(Type t) => t == typeof(decimal) || (t.IsPrimitive && t != typeof(bool) && t != typeof(char));
    private static bool IsBasicType(Type t) => t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(decimal) || t == typeof(DateTime) || t == typeof(Guid) || (Nullable.GetUnderlyingType(t) != null && IsBasicType(Nullable.GetUnderlyingType(t)));
    private static bool IsDictionary(Type t) => t.IsGenericType && (t.GetGenericTypeDefinition() == typeof(Dictionary<,>) || t.GetGenericTypeDefinition() == typeof(IDictionary<,>));
    private static bool IsEnumerable(Type t) => t != typeof(string) && typeof(IEnumerable).IsAssignableFrom(t);
    private static Type GetElementType(Type t) => t.IsArray ? t.GetElementType() : (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(IEnumerable<>)) ? t.GetGenericArguments()[0] : t.GetInterfaces().FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>))?.GetGenericArguments()[0] ?? typeof(object);
    private static bool IsNullable(Type t) => !t.IsValueType || Nullable.GetUnderlyingType(t) != null;
    private static bool ParseBoolRelaxed(string s) { if (string.IsNullOrWhiteSpace(s)) return false; s = s.Trim(); return s.Equals("1") || s.Equals("yes", StringComparison.OrdinalIgnoreCase) || s.Equals("on", StringComparison.OrdinalIgnoreCase) || (bool.TryParse(s, out var b) && b); }
    private static char ParseCharRelaxed(string s) => !string.IsNullOrEmpty(s) ? s[0] : default(char);

    private static Action<T, P> GetSetter<T, P>(PropertyInfo p)
    {
        if (_setterCache.TryGetValue(p, out var d)) return (Action<T, P>)d;
        var t = Expression.Parameter(typeof(T));
        var v = Expression.Parameter(typeof(P));
        var c = Expression.Lambda<Action<T, P>>(Expression.Assign(Expression.Property(t, p), v), t, v).Compile();
        _setterCache.TryAdd(p, c);
        return c;
    }

    public interface IMapConfiguration<S, D> { IMapConfiguration<S, D> ForMember<M>(Expression<Func<D, M>> d, Func<S, M> s); IMapConfiguration<S, D> Ignore<M>(Expression<Func<D, M>> d); }
    private class MapConfiguration<S, D> : IMapConfiguration<S, D>
    {
        private S _s; private D _d;
        public MapConfiguration(S s, D d) { _s = s; _d = d; }
        public IMapConfiguration<S, D> ForMember<M>(Expression<Func<D, M>> d, Func<S, M> s) { var p = GetP(d); if (p != null) GetSetter<D, M>(p)(_d, s(_s)); return this; }
        public IMapConfiguration<S, D> Ignore<M>(Expression<Func<D, M>> d) { var p = GetP(d); if (p != null) GetSetter<D, M>(p)(_d, default); return this; }
        private PropertyInfo GetP<T, P>(Expression<Func<T, P>> e) => ((e.Body as MemberExpression) ?? ((e.Body as UnaryExpression)?.Operand as MemberExpression))?.Member as PropertyInfo;
    }
    #endregion
}

