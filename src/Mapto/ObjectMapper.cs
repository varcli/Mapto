using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;

namespace Mapto;

/// <summary>
/// 高性能、零配置、轻量级对象映射
/// </summary>
public static class ObjectMapper
{
    #region 全局配置
    public static class GlobalConfig
    {
        public static int MaxDepth = 30;
        public static bool MapEmptyStringToDefault = true;
    }
    #endregion

    #region 内部缓存
    [ThreadStatic] private static int _currentDepth;

    private static readonly MethodInfo _mapMethod = typeof(ObjectMapper).GetMethods(BindingFlags.Public | BindingFlags.Static)
        .First(m => m.Name == nameof(Map) && m.IsGenericMethodDefinition && m.GetGenericArguments().Length == 2 && m.GetParameters().Length == 2);
    private static readonly MethodInfo _mapEnum = typeof(ObjectMapper).GetMethod("MapEnumerable", BindingFlags.NonPublic | BindingFlags.Static);
    private static readonly MethodInfo _mapDict = typeof(ObjectMapper).GetMethod("MapDictionary", BindingFlags.NonPublic | BindingFlags.Static);
    private static readonly ConcurrentDictionary<PropertyInfo, Delegate> _setterCache = new ConcurrentDictionary<PropertyInfo, Delegate>();
    #endregion

    #region 公共 API

    public static TDestination Map<TSource, TDestination>(TSource source, Action<IMapConfiguration<TSource, TDestination>> config = null)
    {
        if (source == null) return default(TDestination);
        try
        {
            _currentDepth++;
            if (_currentDepth > GlobalConfig.MaxDepth) return default(TDestination);
            if (config == null) return MapperCache<TSource, TDestination>.MapperFunc(source);

            var destination = MapperCache<TSource, TDestination>.MapperFunc(source);
            if (!MapperCache<TSource, TDestination>.IsBasic)
                config(new MapConfiguration<TSource, TDestination>(source, destination));
            return destination;
        }
        finally { _currentDepth--; }
    }

    public static TDestination Map<TSource, TDestination>(TSource source, TDestination existing, Action<IMapConfiguration<TSource, TDestination>> config = null)
    {
        if (source == null) return existing;
        if (existing == null) return Map(source, config);
        try
        {
            _currentDepth++;
            if (_currentDepth > GlobalConfig.MaxDepth) return existing;
            MapperCache<TSource, TDestination>.MergeAction(source, existing);
            if (config != null && !MapperCache<TSource, TDestination>.IsBasic)
                config(new MapConfiguration<TSource, TDestination>(source, existing));
            return existing;
        }
        finally { _currentDepth--; }
    }

    #endregion

    #region 核心引擎
    private static class MapperCache<TSource, TDestination>
    {
        public static readonly Func<TSource, TDestination> MapperFunc;
        public static readonly Action<TSource, TDestination> MergeAction;
        public static readonly bool IsBasic;

        static MapperCache()
        {
            var sType = typeof(TSource);
            var dType = typeof(TDestination);
            IsBasic = IsBasicType(dType);

            if (IsDictionary(sType) && IsDictionary(dType))
            {
                MapperFunc = CompileDictionaryMapper();
                MergeAction = (s, d) => { var n = MapperFunc(s); CopyDictionary(n, d as IDictionary); };
            }
            else if (IsEnumerable(sType) && IsEnumerable(dType))
            {
                MapperFunc = CompileEnumerableMapper();
                MergeAction = (s, d) => { };
            }
            else if (IsBasicType(sType) || IsBasicType(dType))
            {
                MapperFunc = CompileBasicTypeMapper();
                MergeAction = (s, d) => { };
            }
            else
            {
                MapperFunc = CreateMapDelegate();
                MergeAction = CreateMergeDelegate();
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
            var method = _mapEnum.MakeGenericMethod(itemSrc, itemDest);

            var call = Expression.Call(method, Expression.Convert(p, typeof(IEnumerable<>).MakeGenericType(itemSrc)), Expression.Constant(typeof(TDestination)));
            var castedResult = Expression.Convert(call, typeof(TDestination));

            return Expression.Lambda<Func<TSource, TDestination>>(castedResult, p).Compile();
        }

        private static Func<TSource, TDestination> CompileDictionaryMapper()
        {
            var p = Expression.Parameter(typeof(TSource), "source");
            var argsS = typeof(TSource).GetGenericArguments();
            var argsD = typeof(TDestination).GetGenericArguments();
            var method = _mapDict.MakeGenericMethod(argsS[0], argsS[1], argsD[0], argsD[1]);

            // [关键修复] 添加 Convert 强转: Dictionary -> TDestination (e.g. IDictionary)
            var call = Expression.Call(method, Expression.Convert(p, typeof(IDictionary<,>).MakeGenericType(argsS[0], argsS[1])));
            var castedResult = Expression.Convert(call, typeof(TDestination));

            return Expression.Lambda<Func<TSource, TDestination>>(castedResult, p).Compile();
        }

        private static Func<TSource, TDestination> CreateMapDelegate()
        {
            var p = Expression.Parameter(typeof(TSource), "source");
            var newExp = Expression.New(typeof(TDestination));
            var bindings = new List<MemberBinding>();
            foreach (var dProp in typeof(TDestination).GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!dProp.CanWrite) continue;
                var val = BuildValueExpression(p, typeof(TSource), dProp);
                if (val != null) bindings.Add(Expression.Bind(dProp, val));
            }
            var nullCheck = IsNullable(typeof(TSource))
                ? Expression.Condition(Expression.Equal(p, Expression.Constant(null, typeof(TSource))), Expression.Default(typeof(TDestination)), Expression.MemberInit(newExp, bindings))
                : (Expression)Expression.MemberInit(newExp, bindings);

            return Expression.Lambda<Func<TSource, TDestination>>(nullCheck, p).Compile();
        }

        private static Action<TSource, TDestination> CreateMergeDelegate()
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

        private static void CopyDictionary(object src, IDictionary dest) { if (dest == null || src == null) return; dest.Clear(); foreach (DictionaryEntry e in (src as IDictionary)) dest.Add(e.Key, e.Value); }
    }
    #endregion

    #region 表达式构建逻辑
    private static Expression BuildValueExpression(Expression pSrc, Type sType, PropertyInfo dProp)
    {
        // 检查 NoMap 特性
        if (dProp.GetCustomAttributes(typeof(NoMapAttribute), false).Length > 0)
            return null;

        Expression val = null;
        
        // 检查 MapAs 特性
        var mapAsAttr = dProp.GetCustomAttributes(typeof(MapAsAttribute), false).FirstOrDefault() as MapAsAttribute;
        if (mapAsAttr != null)
        {
            var sourceProp = sType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(p => p.Name.Equals(mapAsAttr.SourcePropertyName, StringComparison.OrdinalIgnoreCase));
            if (sourceProp != null && sourceProp.CanRead)
                val = Expression.Property(pSrc, sourceProp);
        }
        else
        {
            // 默认匹配逻辑
            var direct = sType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(p => p.Name.Equals(dProp.Name, StringComparison.OrdinalIgnoreCase));
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
                if (child == null)
                {
                    var match = prop.PropertyType.GetProperties().FirstOrDefault(p => p.Name.Equals(nextName, StringComparison.OrdinalIgnoreCase));
                    if (match != null) child = Expression.Property(nextExp, match);
                }
                if (child != null)
                {
                    // 仅对可空属性生成 Null 检查
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
        if (dType == sType) return src;
        var uDest = Nullable.GetUnderlyingType(dType) ?? dType;
        var uSrc = Nullable.GetUnderlyingType(sType) ?? sType;

        // 1. 基础类型
        if (IsBasicType(uSrc) && IsBasicType(uDest)) return BuildBasicTypeExpression(src, sType, dType, uSrc, uDest);

        // 2. 集合与字典 (必须显式 Convert)
        if (IsDictionary(sType) && IsDictionary(dType))
        {
            var argsS = sType.GetGenericArguments(); var argsD = dType.GetGenericArguments();
            var call = Expression.Call(_mapDict.MakeGenericMethod(argsS[0], argsS[1], argsD[0], argsD[1]), Expression.Convert(src, typeof(IDictionary<,>).MakeGenericType(argsS[0], argsS[1])));
            return Expression.Convert(call, dType); // [Fix] Cast object -> TDest
        }
        if (IsEnumerable(sType) && IsEnumerable(dType))
        {
            var iS = GetElementType(sType); var iD = GetElementType(dType);
            var call = Expression.Call(_mapEnum.MakeGenericMethod(iS, iD), Expression.Convert(src, typeof(IEnumerable<>).MakeGenericType(iS)), Expression.Constant(dType));
            return Expression.Convert(call, dType); // [Fix] Cast object -> TDest
        }

        // 3. 递归
        if (!IsBasicType(sType) && !IsBasicType(dType))
        {
            return Expression.Call(_mapMethod.MakeGenericMethod(sType, dType), src, Expression.Constant(null, typeof(Action<>).MakeGenericType(typeof(IMapConfiguration<,>).MakeGenericType(sType, dType))));
        }
        return null;
    }

    private static Expression BuildBasicTypeExpression(Expression s, Type sType, Type dType, Type uSrc, Type uDest)
    {
        Expression val = sType != uSrc ? Expression.Property(s, "Value") : s;
        Expression res = null;

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

            if (res != null && uDest.IsValueType && GlobalConfig.MapEmptyStringToDefault)
            {
                var check = typeof(string).GetMethod("IsNullOrWhiteSpace", new[] { typeof(string) });
                res = Expression.Condition(Expression.Call(check, s), Expression.Default(dType), dType != uDest ? Expression.Convert(res, dType) : res);
                return res;
            }
        }

        if (res == null)
        {
            if ((IsNumeric(uSrc) && IsNumeric(uDest)) || (uSrc.IsEnum || uDest.IsEnum)) res = Expression.Convert(val, uDest);
            else if (dType == typeof(string))
            {
                var m = typeof(object).GetMethod("ToString");
                if (IsNullable(sType))
                    res = Expression.Condition(Expression.Equal(s, Expression.Constant(null, sType)), Expression.Constant(null, typeof(string)), Expression.Call(s, m));
                else
                    res = Expression.Call(s, m);
                return res;
            }
        }

        if (res == null) return null;
        if (dType != uDest && res.Type == uDest) res = Expression.Convert(res, dType);
        if (sType != uSrc) return Expression.Condition(Expression.Property(s, "HasValue"), res, Expression.Default(dType));
        return res;
    }
    #endregion

    #region 辅助方法
    private static bool IsNumeric(Type t) => t == typeof(decimal) || (t.IsPrimitive && t != typeof(bool) && t != typeof(char));
    private static bool IsBasicType(Type t) => t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(decimal) || t == typeof(DateTime) || t == typeof(Guid) || (Nullable.GetUnderlyingType(t) != null && IsBasicType(Nullable.GetUnderlyingType(t)));
    private static bool IsDictionary(Type t) => t.IsGenericType && (t.GetGenericTypeDefinition() == typeof(Dictionary<,>) || t.GetGenericTypeDefinition() == typeof(IDictionary<,>));
    private static bool IsEnumerable(Type t) => t != typeof(string) && typeof(IEnumerable).IsAssignableFrom(t);
    private static Type GetElementType(Type t) => t.IsArray ? t.GetElementType() : (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(IEnumerable<>)) ? t.GetGenericArguments()[0] : t.GetInterfaces().FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>))?.GetGenericArguments()[0] ?? typeof(object);
    private static bool IsNullable(Type t) => !t.IsValueType || Nullable.GetUnderlyingType(t) != null;

    private static bool ParseBoolRelaxed(string s) { if (string.IsNullOrWhiteSpace(s)) return false; s = s.Trim(); return s.Equals("1") || s.Equals("yes", StringComparison.OrdinalIgnoreCase) || s.Equals("on", StringComparison.OrdinalIgnoreCase) || (bool.TryParse(s, out var b) && b); }
    private static char ParseCharRelaxed(string s) => !string.IsNullOrEmpty(s) ? s[0] : default(char);

    // 返回 object, 但必须在 Tree 中 Cast
    private static object MapEnumerable<TSrc, TDest>(IEnumerable<TSrc> src, Type dCollType)
    {
        if (src == null) return dCollType.IsArray ? (object)new TDest[0] : new List<TDest>();
        var func = MapperCache<TSrc, TDest>.MapperFunc;
        var list = new List<TDest>(); foreach (var i in src) list.Add(func(i));
        if (dCollType.IsArray) return list.ToArray();
        if (dCollType.IsGenericType && (dCollType.GetGenericTypeDefinition() == typeof(HashSet<>) || dCollType.GetGenericTypeDefinition() == typeof(ISet<>))) return new HashSet<TDest>(list);
        return list;
    }
    private static Dictionary<DK, DV> MapDictionary<SK, SV, DK, DV>(IDictionary<SK, SV> src)
    {
        if (src == null) return new Dictionary<DK, DV>();
        var fk = MapperCache<SK, DK>.MapperFunc; var fv = MapperCache<SV, DV>.MapperFunc;
        var res = new Dictionary<DK, DV>(); foreach (var kv in src) { var k = fk(kv.Key); if (k != null && !res.ContainsKey(k)) res.Add(k, fv(kv.Value)); }
        return res;
    }

    private static Action<T, P> GetSetter<T, P>(PropertyInfo p) { if (_setterCache.TryGetValue(p, out var d)) return (Action<T, P>)d; var t = Expression.Parameter(typeof(T)); var v = Expression.Parameter(typeof(P)); var c = Expression.Lambda<Action<T, P>>(Expression.Assign(Expression.Property(t, p), v), t, v).Compile(); _setterCache.TryAdd(p, c); return c; }
    public interface IMapConfiguration<S, D> { IMapConfiguration<S, D> ForMember<M>(Expression<Func<D, M>> d, Func<S, M> s); IMapConfiguration<S, D> Ignore<M>(Expression<Func<D, M>> d); }
    private class MapConfiguration<S, D> : IMapConfiguration<S, D> { private S _s; private D _d; public MapConfiguration(S s, D d) { _s = s; _d = d; } public IMapConfiguration<S, D> ForMember<M>(Expression<Func<D, M>> d, Func<S, M> s) { var p = GetP(d); if (p != null) GetSetter<D, M>(p)(_d, s(_s)); return this; } public IMapConfiguration<S, D> Ignore<M>(Expression<Func<D, M>> d) { var p = GetP(d); if (p != null) GetSetter<D, M>(p)(_d, default); return this; } private PropertyInfo GetP<T, P>(Expression<Func<T, P>> e) => ((e.Body as MemberExpression) ?? ((e.Body as UnaryExpression)?.Operand as MemberExpression))?.Member as PropertyInfo; }
    #endregion
}

