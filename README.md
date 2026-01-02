# Mapto

一个极简、极速、零配置的 .NET 对象映射库。
单文件封装，无第三方依赖。

## ✨ 核心特性

* **极速性能**: 基于 Expression Tree + 泛型静态缓存 (Generic Static Cache)，消除反射开销。
* **零配置**: 自动匹配同名属性，支持忽略大小写。
* **智能扁平化**: 自动映射 `Dest.CustomerName` -> `Source.Customer.Name`。
* **空值防御**: 自动处理 Null 引用，防止 `NullReferenceException`。
* **宽容类型转换**: 
    * `String` -> `int/long/double/decimal` (空字符串自动转默认值)
    * `String` -> `Guid/DateTime/Enum`
    * `String` -> `bool` ("1", "yes", "true", "on")
* **扩展方法支持**: 丝滑的链式调用 `.To<Target>()`。
* **更新已有对象**: 支持 `source.MapTo(existing)`，适用于 ORM 更新场景。
* **循环引用保护**: 内置递归深度限制。

## 🚀 快速开始

### 1. 基础映射

```csharp
var entity = new UserEntity { Id = 1, Name = "Admin" };

// 方式 A: 静态方法
var dto = ObjectMapper.Map<UserEntity, UserDto>(entity);

// 方式 B: 扩展方法 (推荐)
var dto = entity.To<UserDto>();