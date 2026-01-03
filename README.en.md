# 🚀 Mapto

<div align="center">

> "Type less, map more."

[简体中文](./README.md) | [English](./README.en.md)

</div>

A minimalist, blazing-fast, zero-configuration .NET object mapping library.
Single-file implementation with no third-party dependencies.

## 📖 Introduction

**Mapto** is designed for developers who value **simplicity**, **speed**, and **intelligence**.

Unlike traditional heavyweight mapping libraries, Mapto is extremely lightweight. Built on **Expression Trees** and **Generic Static Cache** technology, it delivers performance comparable to hand-written native code (approximately 3x faster than traditional reflection-based mapping).

It consists of **a single file** that you can directly embed into your project.

## ✨ Core Features

* **Blazing Performance**: Based on Expression Trees + Generic Static Cache, eliminating reflection overhead.
* **Zero Configuration**: Automatically matches properties by name, supports case-insensitive matching.
* **Smart Flattening**: Automatically maps `Dest.CustomerName` -> `Source.Customer.Name`.
* **Null Safety**: Automatically handles null references, preventing `NullReferenceException`.
* **Flexible Type Conversion**: 
    * `String` -> `int/long/double/decimal` (empty strings convert to default values)
    * `String` -> `Guid/DateTime/Enum`
    * `String` -> `bool` ("1", "yes", "true", "on")
* **Extension Method Support**: Fluent chaining with `source.Map<Target>()`.
* **Update Existing Objects**: Supports `source.MapTo(existing)`, ideal for ORM update scenarios.
* **Circular Reference Protection**: Built-in recursion depth limit.
* **EF Core Support**: Native support for `IQueryable` projection (`.ProjectTo<T>()`), generating clean SQL.

## 🚀 Quick Start

### 1. Basic Mapping

```csharp
var entity = new UserEntity { Id = 1, Name = "Admin" };

// Method A: Static method
var dto = ObjectMapper.Map<UserEntity, UserDto>(entity);

// Method B: Extension method (Recommended)
var dto = entity.Map<UserDto>();
```
